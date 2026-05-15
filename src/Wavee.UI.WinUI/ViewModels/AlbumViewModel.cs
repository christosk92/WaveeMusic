using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
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
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Sort column options for album tracks.
/// </summary>
public enum AlbumSortColumn { Title, Artist, TrackNumber }

/// <summary>
/// Header payload for one disc in a multi-disc album. Bound by the
/// <c>TrackDataGrid.GroupHeaderTemplate</c> in <c>AlbumPage.xaml</c>.
/// </summary>
public sealed record DiscGroupHeader(int Number, string TitleText, string DurationFormatted);

/// <summary>
/// ViewModel for the Album detail page.
/// Album tracks are static after load — no reactive pipeline needed.
/// </summary>
public sealed partial class AlbumViewModel : ObservableObject, ITrackListViewModel, ITabBarItemContent, IDisposable
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
    private bool _isSaved;
    private bool _isLoading;
    private bool _isLoadingTracks;
    private bool _hasError;
    private string? _errorMessage;

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

    /// <summary>"Similar albums" (For this mood) — track-seeded recommendations.</summary>
    private readonly ObservableCollection<AlbumSimilarResult> _similarAlbums = [];

    /// <summary>"Fans also like" — related artists for the primary album artist.</summary>
    private readonly ObservableCollection<RelatedArtistResult> _similarArtists = [];

    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    /// <summary>
    /// The album ID used for navigation, store lookup, and playback context.
    /// </summary>
    public string AlbumId
    {
        get => _albumId;
        private set => SetProperty(ref _albumId, value);
    }

    private AlbumView? _album;

    public AlbumView? Album
    {
        get => _album;
        private set
        {
            if (EqualityComparer<AlbumView?>.Default.Equals(_album, value))
                return;

            var old = _album;
            _album = value;
            var paletteChanged = !Equals(old?.Palette, value?.Palette);
            var shareChanged = !string.Equals(old?.ShareUrl, value?.ShareUrl, StringComparison.Ordinal);

            if (paletteChanged)
                ApplyTheme(_isDarkTheme);

            OnPropertyChanged(nameof(Album));
            RaiseAlbumEnvelopeDependents();

            if (!string.Equals(old?.Name, value?.Name, StringComparison.Ordinal))
                UpdateTabTitle();

            if (shareChanged)
                ShareCommand.NotifyCanExecuteChanged();
        }
    }

    private static readonly string[] AlbumEnvelopeDependentProperties =
    [
        nameof(AlbumName), nameof(AlbumImageUrl), nameof(ColorHex), nameof(ArtistId), nameof(ArtistName),
        nameof(ArtistImageUrl), nameof(Artists), nameof(AllDistinctArtists), nameof(HasMultipleArtists),
        nameof(ArtistAvatarItems), nameof(OverflowArtistCount), nameof(HeaderArtistLinks), nameof(Year),
        nameof(AlbumType), nameof(AlbumTypeUpper), nameof(Label), nameof(ReleaseDateFormatted),
        nameof(CopyrightsText), nameof(IsPreRelease), nameof(PreReleaseEndDateTime),
        nameof(PreReleaseFormatted), nameof(PreReleaseRelative), nameof(ShareUrl), nameof(CanShare),
        nameof(MetaInlineLine), nameof(Palette)
    ];

    private void RaiseAlbumEnvelopeDependents()
    {
        foreach (var propertyName in AlbumEnvelopeDependentProperties)
            OnPropertyChanged(propertyName);
    }

    private AlbumView EmptyAlbumEnvelope()
        => new(
            Id: AlbumId,
            Name: string.Empty,
            ImageUrl: null,
            ColorHex: null,
            ArtistId: string.Empty,
            ArtistName: string.Empty,
            ArtistImageUrl: null,
            Artists: [],
            AllDistinctArtists: [],
            ArtistAvatarItems: [],
            HeaderArtistLinks: [],
            OverflowArtistCount: 0,
            Year: 0,
            Type: null,
            Label: null,
            ReleaseDateFormatted: null,
            CopyrightsText: null,
            IsPreRelease: false,
            PreReleaseEndDateTime: null,
            PreReleaseFormatted: null,
            PreReleaseRelative: null,
            ShareUrl: null,
            MetaInlineLine: null,
            Palette: null);

    /// <summary>
    /// The album name.
    /// </summary>
    public string AlbumName => Album?.Name ?? string.Empty;

    /// <summary>
    /// The album cover image URL.
    /// </summary>
    public string? AlbumImageUrl => Album?.ImageUrl;

    /// <summary>
    /// Extracted dark color from the album cover art, as a hex string.
    /// Used as a tint for track placeholder backgrounds while album art loads.
    /// </summary>
    public string? ColorHex => Album?.ColorHex;

    /// <summary>
    /// The primary artist ID.
    /// </summary>
    public string ArtistId => Album?.ArtistId ?? string.Empty;

    /// <summary>
    /// The primary artist name.
    /// </summary>
    public string ArtistName => Album?.ArtistName ?? string.Empty;

    /// <summary>
    /// The primary artist's avatar image URL, surfaced from the album's
    /// <c>artists.items[0].visuals.avatarImage</c> so the page can render a small
    /// circular thumbnail next to the artist name without a second fetch.
    /// </summary>
    public string? ArtistImageUrl => Album?.ArtistImageUrl;

    /// <summary>
    /// Album-billed artists (from <c>albumUnion.artists.items</c>). Drives the
    /// stacked-avatar strip in the header and the inline names line below it.
    /// </summary>
    public IReadOnlyList<AlbumArtistResult> Artists => Album?.Artists ?? [];

    /// <summary>
    /// Every distinct artist on the album: billed artists first, then track-only
    /// contributors deduped by URI. Drives the artists Flyout opened from the
    /// avatar stack so users can navigate to featured guests not in the billing.
    /// </summary>
    public IReadOnlyList<AlbumArtistResult> AllDistinctArtists => Album?.AllDistinctArtists ?? [];

    /// <summary>
    /// Avatar items for the header <c>AvatarStack</c>, projected from
    /// <see cref="Artists"/>. Pre-projected here so the XAML can bind directly
    /// without a converter per page.
    /// </summary>
    public IReadOnlyList<AvatarStackItem> ArtistAvatarItems => Album?.ArtistAvatarItems ?? [];

    /// <summary>
    /// Number of additional distinct artists beyond <see cref="Artists"/>.
    /// Drives the trailing <c>+N</c> badge on the avatar stack - non-zero when
    /// the album has track-only contributors that aren't in the billing.
    /// </summary>
    public int OverflowArtistCount => Album?.OverflowArtistCount ?? 0;

    /// <summary>
    /// Per-name link projections for the header artists line. Each entry
    /// carries <c>IsFirst</c> so the comma separator preceding the entry can
    /// hide on item 0 without page-side index logic.
    /// </summary>
    public IReadOnlyList<HeaderArtistLink> HeaderArtistLinks => Album?.HeaderArtistLinks ?? [];

    /// <summary>
    /// True when the album has more than one distinct artist anywhere
    /// (billed + per-track unioned). Soundtracks, compilations, and any
    /// album with featured guests are detected here. Drives the per-row
    /// artist column on the album track grid - the default suppresses it on
    /// album pages because most albums are single-artist, but for collabs
    /// the artist names are essential context.
    /// </summary>
    public bool HasMultipleArtists => AllDistinctArtists.Count > 1;

    /// <summary>
    /// Release year.
    /// </summary>
    public int Year => Album?.Year ?? 0;

    /// <summary>
    /// Album type (Album, Single, EP, etc.).
    /// </summary>
    public string? AlbumType => Album?.Type;

    public string? Label => Album?.Label;

    public string? ReleaseDateFormatted => Album?.ReleaseDateFormatted;

    public string? CopyrightsText => Album?.CopyrightsText;
    /// <summary>
    /// Whether the album is saved to the user's library.
    /// </summary>
    public bool IsSaved
    {
        get => _isSaved;
        set => SetProperty(ref _isSaved, value);
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
            SetProperty(ref _isLoading, value);
        }
    }

    /// <summary>
    /// Loading state specifically for tracks.
    /// </summary>
    public bool IsLoadingTracks
    {
        get => _isLoadingTracks;
        set => SetProperty(ref _isLoadingTracks, value);
    }

    /// <summary>
    /// Whether an error occurred during loading.
    /// </summary>
    public bool HasError
    {
        get => _hasError;
        set => SetProperty(ref _hasError, value);
    }

    /// <summary>
    /// Error message to display.
    /// </summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
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
            SetProperty(ref _searchQuery, value);
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
            SetProperty(ref _currentSortColumn, value);
            if (old != value)
            {
                OnPropertyChanged(nameof(IsSortingByTitle));
                OnPropertyChanged(nameof(IsSortingByArtist));
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
            SetProperty(ref _isSortDescending, value);
            if (old != value)
            {
                OnPropertyChanged(nameof(SortChevronGlyph));
            }
        }
    }

    /// <summary>
    /// Total number of tracks.
    /// </summary>
    public int TotalTracks
    {
        get => _totalTracks;
        private set => SetProperty(ref _totalTracks, value);
    }

    /// <summary>
    /// Total duration formatted.
    /// </summary>
    public string TotalDuration
    {
        get => _totalDuration;
        private set => SetProperty(ref _totalDuration, value);
    }

    /// <summary>
    /// Currently selected items in the ListView.
    /// </summary>
    public IReadOnlyList<object> SelectedItems
    {
        get => _selectedItems;
        set
        {
            SetProperty(ref _selectedItems, value);
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionHeaderText));
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
        private set => SetProperty(ref _hasMoreByArtist, value);
    }

    private bool _hasNoRelatedAlbums;
    public bool HasNoRelatedAlbums
    {
        get => _hasNoRelatedAlbums;
        private set => SetProperty(ref _hasNoRelatedAlbums, value);
    }

    /// <summary>
    /// Merchandise items for this album.
    /// </summary>
    public IReadOnlyList<AlbumMerchItemResult> MerchItems => _merchItems;

    public bool HasMerch => MerchItems.Count > 0;

    /// <summary>Similar albums (track-seeded) — drives the AlbumPage "For this mood" shelf.</summary>
    public IReadOnlyList<AlbumSimilarResult> SimilarAlbums => _similarAlbums;

    private bool _hasSimilarAlbums;
    public bool HasSimilarAlbums
    {
        get => _hasSimilarAlbums;
        private set => SetProperty(ref _hasSimilarAlbums, value);
    }

    /// <summary>Related artists for the album's primary artist — Fans also like.</summary>
    public IReadOnlyList<RelatedArtistResult> SimilarArtists => _similarArtists;

    private bool _hasSimilarArtists;
    public bool HasSimilarArtists
    {
        get => _hasSimilarArtists;
        private set => SetProperty(ref _hasSimilarArtists, value);
    }

    /// <summary>~150 char excerpt of the primary artist's biography — mini About card.</summary>
    private string? _artistBioExcerpt;
    public string? ArtistBioExcerpt
    {
        get => _artistBioExcerpt;
        private set
        {
            SetProperty(ref _artistBioExcerpt, value);
            OnPropertyChanged(nameof(HasArtistBioExcerpt));
        }
    }

    public bool HasArtistBioExcerpt => !string.IsNullOrWhiteSpace(_artistBioExcerpt);

    /// <summary>True when the album's lead track has at least one music-video association.
    /// Used to gate the "Watch the official video" CTA on single-track releases.</summary>
    private bool _hasMusicVideo;
    public bool HasMusicVideo
    {
        get => _hasMusicVideo;
        private set => SetProperty(ref _hasMusicVideo, value);
    }

    /// <summary>Source-track URI for the music-video promotion strip. The video
    /// track itself is resolved at click time through the music-video metadata service.</summary>
    private string? _musicVideoUri;
    public string? MusicVideoUri
    {
        get => _musicVideoUri;
        private set => SetProperty(ref _musicVideoUri, value);
    }

    // ── Alternate releases (deluxe / remaster / anniversary editions of THIS album) ──

    private readonly ObservableCollection<AlbumAlternateReleaseResult> _alternateReleases = [];
    public IReadOnlyList<AlbumAlternateReleaseResult> AlternateReleases => _alternateReleases;

    private bool _hasAlternateReleases;
    public bool HasAlternateReleases
    {
        get => _hasAlternateReleases;
        private set => SetProperty(ref _hasAlternateReleases, value);
    }

    // ── Pre-release ──

    public bool IsPreRelease => Album?.IsPreRelease == true;

    public DateTimeOffset? PreReleaseEndDateTime => Album?.PreReleaseEndDateTime;

    /// <summary>"Coming Friday, May 2 at 22:00" — formatted local time. Null when not pre-release.</summary>
    public string? PreReleaseFormatted => Album?.PreReleaseFormatted;

    /// <summary>"in 3 days" / "in 4 hours" — caption on the right of the banner.</summary>
    public string? PreReleaseRelative => Album?.PreReleaseRelative;

    // ── Share ──

    public string? ShareUrl => Album?.ShareUrl;

    public bool CanShare => !string.IsNullOrEmpty(ShareUrl);

    // ── Theme-aware palette (from the album cover) ──

    public AlbumPalette? Palette => Album?.Palette;
    private bool _isDarkTheme;

    /// <summary>Subtle page-wash brush tinted toward the album's color. Null when no palette.</summary>
    private Brush? _paletteBackdropBrush;
    public Brush? PaletteBackdropBrush
    {
        get => _paletteBackdropBrush;
        private set => SetProperty(ref _paletteBackdropBrush, value);
    }

    /// <summary>Gradient brush used on the hero ink overlay (palette-tinted, theme-aware).</summary>
    private Brush? _paletteHeroGradientBrush;
    public Brush? PaletteHeroGradientBrush
    {
        get => _paletteHeroGradientBrush;
        private set => SetProperty(ref _paletteHeroGradientBrush, value);
    }

    /// <summary>Accent pill background brush (album type pill in the hero). Null falls back to system accent.</summary>
    private Brush? _paletteAccentPillBrush;
    public Brush? PaletteAccentPillBrush
    {
        get => _paletteAccentPillBrush;
        private set => SetProperty(ref _paletteAccentPillBrush, value);
    }

    private Brush? _paletteAccentPillForegroundBrush;
    public Brush? PaletteAccentPillForegroundBrush
    {
        get => _paletteAccentPillForegroundBrush;
        private set => SetProperty(ref _paletteAccentPillForegroundBrush, value);
    }

    // ── Hero meta line ("12 songs · 38 min · 1980") ──

    public string? MetaInlineLine => Album?.MetaInlineLine;

    /// <summary>"ALBUM" / "SINGLE" / "EP" / "COMPILATION", upper-cased for the hero pill.</summary>
    public string AlbumTypeUpper => AlbumType?.ToUpperInvariant() ?? "ALBUM";

    /// <summary>
    /// Filtered and sorted tracks for UI binding. Stable instance — mutate via
    /// <see cref="ObservableCollectionExtensions.ReplaceWith{T}"/> / Clear so the
    /// bound ListView keeps its CollectionChanged subscription across navs.
    /// </summary>
    private readonly ObservableCollection<LazyTrackItem> _filteredTracks = [];
    public IReadOnlyList<LazyTrackItem> FilteredTracks => _filteredTracks;

    // Per-disc totals computed once whenever _allTracks is replaced. Used by the
    // disc-grouping header in AlbumPage to render "Disc N · X songs · M min".
    private IReadOnlyDictionary<int, TimeSpan> _discDurations =
        new Dictionary<int, TimeSpan>();
    private bool _isMultiDisc;

    /// <summary>
    /// True when this album has more than one disc. Drives <c>IsGrouped</c> on
    /// the track grid so single-disc albums keep today's flat list.
    /// </summary>
    public bool IsMultiDisc
    {
        get => _isMultiDisc;
        private set => SetProperty(ref _isMultiDisc, value);
    }

    /// <summary>
    /// Stringified disc number used as the group key by <c>TrackDataGrid</c>.
    /// </summary>
    public Func<ITrackItem, object?> DiscGroupKeySelector { get; }

    /// <summary>
    /// Builds the <see cref="DiscGroupHeader"/> payload for one disc, looking up
    /// the cached total duration so the header can render "Disc N · M min".
    /// Called once per group with the group's first item.
    /// </summary>
    public Func<ITrackItem, object> DiscGroupHeaderSelector { get; }

    /// <summary>
    /// "N songs" formatter for the disc header. Singular for one-track discs.
    /// </summary>
    public Func<int, string> DiscGroupCountFormatter { get; }

    // Sort indicator properties for column headers
    public bool IsSortingByTitle => CurrentSortColumn == AlbumSortColumn.Title;
    public bool IsSortingByArtist => CurrentSortColumn == AlbumSortColumn.Artist;
    public bool IsSortingByAlbum => false;
    public bool IsSortingByAddedAt => false;

    /// <summary>
    /// Sort chevron glyph: up for ascending, down for descending.
    /// </summary>
    public string SortChevronGlyph => IsSortDescending ? "\uE70D" : "\uE70E";

    private readonly IMusicVideoMetadataService? _musicVideoMetadata;

    public AlbumViewModel(
        IAlbumService albumService,
        AlbumStore albumStore,
        ILibraryDataService libraryDataService,
        IPlaybackStateService playbackStateService,
        ITrackLikeService? likeService = null,
        IMusicVideoMetadataService? musicVideoMetadata = null,
        ILogger<AlbumViewModel>? logger = null)
    {
        _albumService = albumService;
        _albumStore = albumStore;
        _libraryDataService = libraryDataService;
        _playbackStateService = playbackStateService;
        _likeService = likeService;
        _musicVideoMetadata = musicVideoMetadata;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _logger = logger;

        DiscGroupKeySelector = item =>
        {
            var disc = (item is LazyTrackItem l && l.Data is AlbumTrackDto d)
                ? d.DiscNumber
                : 1;
            return disc.ToString(CultureInfo.InvariantCulture);
        };

        DiscGroupHeaderSelector = item =>
        {
            var disc = (item is LazyTrackItem l && l.Data is AlbumTrackDto d)
                ? d.DiscNumber
                : 1;
            var duration = _discDurations.TryGetValue(disc, out var ts)
                ? ts
                : TimeSpan.Zero;
            return new DiscGroupHeader(
                disc,
                $"Disc {disc.ToString(CultureInfo.InvariantCulture)}",
                FormatDuration(duration.TotalSeconds));
        };

        DiscGroupCountFormatter = n => n == 1 ? "1 song" : $"{n:N0} songs";

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
            // Clear per-album scalar detail as one envelope replacement so the
            // cached page cannot paint the previous album's hero metadata while
            // the new store value is replaying. PrefillFrom, called immediately
            // after Activate, re-fills title/cover/artist when the navigation
            // parameter already knows them.
            var previousAlbum = Album;
            Album = preserveHeaderPrefill && previousAlbum is not null
                ? previousAlbum with
                {
                    Id = albumId,
                    ColorHex = null,
                    ArtistImageUrl = null,
                    ArtistId = string.Empty,
                    Artists = [],
                    AllDistinctArtists = [],
                    ArtistAvatarItems = [],
                    HeaderArtistLinks = [],
                    OverflowArtistCount = 0,
                    Year = 0,
                    Type = null,
                    Label = null,
                    ReleaseDateFormatted = null,
                    CopyrightsText = null,
                    ShareUrl = null,
                    IsPreRelease = false,
                    PreReleaseEndDateTime = null,
                    PreReleaseFormatted = null,
                    PreReleaseRelative = null,
                    MetaInlineLine = null,
                    Palette = null
                }
                : new AlbumView(
                    Id: albumId,
                    Name: string.Empty,
                    ImageUrl: null,
                    ColorHex: null,
                    ArtistId: string.Empty,
                    ArtistName: string.Empty,
                    ArtistImageUrl: null,
                    Artists: [],
                    AllDistinctArtists: [],
                    ArtistAvatarItems: [],
                    HeaderArtistLinks: [],
                    OverflowArtistCount: 0,
                    Year: 0,
                    Type: null,
                    Label: null,
                    ReleaseDateFormatted: null,
                    CopyrightsText: null,
                    IsPreRelease: false,
                    PreReleaseEndDateTime: null,
                    PreReleaseFormatted: null,
                    PreReleaseRelative: null,
                    ShareUrl: null,
                    MetaInlineLine: null,
                    Palette: null);
            ClearSecondaryAlbumSections();
            // Force the page + grid back into the loading/skeleton states so the
            // cached page doesn't paint old tracks under the new header during the
            // gap between Activate and the AlbumStore's first push.
            IsLoading = !preserveHeaderPrefill || string.IsNullOrEmpty(AlbumImageUrl);
            IsLoadingTracks = true;
            _allTracks = [];
            RebuildDiscMetadata();
            _popularTrackIds.Clear();
            if (_filteredTracks.Count > 0)
                _filteredTracks.Clear();
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
        RebuildDiscMetadata();
        _popularTrackIds.Clear();
        ClearSecondaryAlbumSections();
    }

    private void ClearSecondaryAlbumSections()
    {
        _alternateReleases.Clear();
        OnPropertyChanged(nameof(AlternateReleases));
        HasAlternateReleases = false;

        _moreByArtist.Clear();
        HasMoreByArtist = false;
        HasNoRelatedAlbums = false;

        _merchItems.Clear();
        OnPropertyChanged(nameof(HasMerch));

        _similarAlbums.Clear();
        HasSimilarAlbums = false;

        _similarArtists.Clear();
        HasSimilarArtists = false;
        ArtistBioExcerpt = null;

        HasMusicVideo = false;
        MusicVideoUri = null;
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

    /// <summary>
    /// Recomputes <see cref="IsMultiDisc"/> and the per-disc total durations
    /// used by the disc-group header. Call after <c>_allTracks</c> is replaced.
    /// </summary>
    private void RebuildDiscMetadata()
    {
        if (_allTracks.Count == 0)
        {
            _discDurations = new Dictionary<int, TimeSpan>();
            IsMultiDisc = false;
            return;
        }

        var byDisc = new Dictionary<int, TimeSpan>();
        foreach (var t in _allTracks)
        {
            var disc = (t.Data as AlbumTrackDto)?.DiscNumber ?? 1;
            byDisc[disc] = byDisc.TryGetValue(disc, out var acc)
                ? acc + t.Duration
                : t.Duration;
        }

        _discDurations = byDisc;
        IsMultiDisc = byDisc.Count > 1;
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

        var tier = Palette is null
            ? null
            : (isDark
                ? (Palette.HigherContrast ?? Palette.HighContrast)
                : (Palette.HighContrast ?? Palette.HigherContrast));

        if (tier == null)
        {
            PaletteBackdropBrush = null;
            PaletteHeroGradientBrush = null;
            PaletteAccentPillBrush = ResolveSystemBrush("AccentFillColorDefaultBrush");
            PaletteAccentPillForegroundBrush = ResolveSystemBrush("TextOnAccentFillColorPrimaryBrush");
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

    private static Brush? ResolveSystemBrush(string resourceKey)
    {
        if (Microsoft.UI.Xaml.Application.Current?.Resources is { } res
            && res.TryGetValue(resourceKey, out var value)
            && value is Brush brush)
            return brush;

        return null;
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
        var current = Album ?? EmptyAlbumEnvelope();
        Album = current with
        {
            Id = !string.IsNullOrEmpty(nav.Uri) ? nav.Uri : current.Id,
            Name = !string.IsNullOrEmpty(nav.Title)
                ? nav.Title
                : clearMissing ? string.Empty : current.Name,
            ImageUrl = !string.IsNullOrEmpty(nav.ImageUrl)
                ? nav.ImageUrl
                : clearMissing ? null : current.ImageUrl,
            ArtistName = !string.IsNullOrEmpty(nav.Subtitle)
                ? nav.Subtitle
                : clearMissing ? string.Empty : current.ArtistName
        };
    }

    /// <summary>
    /// Apply a pre-fetched AlbumDetailResult from the AlbumStore. Called by
    /// ApplyDetailState once the store emits Ready; drives tracklist build,
    /// related-albums, and the non-blocking merch fetch.
    /// </summary>
    private Task ApplyDetailAsync(AlbumDetailResult detail, string albumId)
    {
        if (_disposed || AlbumId != albumId) return Task.CompletedTask;
        _appliedDetailFor = albumId;
        HasError = false;
        ErrorMessage = null;

        try
        {
            // Rootlist for "Add to playlist" — fire-and-forget so it doesn't block detail render.
            _ = LoadRootlistAsync();

            // Build real track list first so the envelope can include the final
            // hero meta line in the same atomic assignment as the scalar metadata.
            _allTracks = detail.Tracks
                .Select((t, i) => LazyTrackItem.Loaded(t.Id, i + 1, t))
                .ToList();
            RebuildDiscMetadata();
            _popularTrackIds = BuildPopularTrackIdSet(detail.Tracks);

            TotalTracks = _allTracks.Count;
            var totalSeconds = _allTracks.Sum(t => t.Duration.TotalSeconds);
            TotalDuration = FormatDuration(totalSeconds);
            IsLoadingTracks = false;

            var current = Album ?? EmptyAlbumEnvelope();
            var firstArtist = detail.Artists.FirstOrDefault();
            var billed = detail.Artists ?? [];
            var avatarItems = billed
                .Select(a => new AvatarStackItem(a.Name ?? "", a.ImageUrl))
                .ToList();
            var headerArtistLinks = billed
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
            var allDistinctArtists = billed.Concat(trackExtras).ToList();
            var year = detail.ReleaseDate.Year > 0 ? detail.ReleaseDate.Year : current.Year;
            var releaseDateFormatted = detail.ReleaseDate != default
                ? detail.ReleaseDate.ToString("MMMM d, yyyy")
                : current.ReleaseDateFormatted;
            var copyrightsText = detail.Copyrights?.Count > 0
                ? string.Join("\n", detail.Copyrights.Select(c =>
                {
                    var prefix = c.Type == "P" ? "\u2117" : "\u00A9";
                    var text = c.Text?.TrimStart() ?? "";
                    if (text.StartsWith("\u00A9") || text.StartsWith("\u2117"))
                        return text;
                    return $"{prefix} {text}";
                }))
                : current.CopyrightsText;
            var (preReleaseFormatted, preReleaseRelative) = FormatPreRelease(detail.IsPreRelease, detail.PreReleaseEndDateTime);

            Album = current with
            {
                Id = albumId,
                Name = !string.IsNullOrEmpty(detail.Name) ? detail.Name : current.Name,
                ImageUrl = !string.IsNullOrEmpty(detail.CoverArtUrl) && string.IsNullOrEmpty(current.ImageUrl)
                    ? detail.CoverArtUrl
                    : current.ImageUrl,
                ColorHex = detail.ColorDarkHex,
                ArtistId = !string.IsNullOrEmpty(firstArtist?.Uri) ? firstArtist!.Uri! : current.ArtistId,
                ArtistName = !string.IsNullOrEmpty(firstArtist?.Name) ? firstArtist!.Name! : current.ArtistName,
                ArtistImageUrl = !string.IsNullOrEmpty(firstArtist?.ImageUrl) ? firstArtist!.ImageUrl : current.ArtistImageUrl,
                Artists = billed,
                ArtistAvatarItems = avatarItems,
                HeaderArtistLinks = headerArtistLinks,
                AllDistinctArtists = allDistinctArtists,
                OverflowArtistCount = trackExtras.Count,
                Year = year,
                Type = !string.IsNullOrEmpty(detail.Type) ? detail.Type : current.Type,
                Label = detail.Label,
                ReleaseDateFormatted = releaseDateFormatted,
                CopyrightsText = copyrightsText,
                IsPreRelease = detail.IsPreRelease,
                PreReleaseEndDateTime = detail.PreReleaseEndDateTime,
                PreReleaseFormatted = preReleaseFormatted,
                PreReleaseRelative = preReleaseRelative,
                ShareUrl = detail.ShareUrl,
                MetaInlineLine = BuildMetaInlineLine(TotalTracks, totalSeconds, year),
                Palette = detail.Palette
            };

            IsSaved = detail.IsSaved;
            RefreshSaveState();
            IsLoading = false;
            // Light the music-video badge on rows whose Spotify track is linked
            // to a local music-video file. Fire-and-forget; the DTO's
            // HasLinkedLocalVideo setter raises PropertyChanged so TrackItem
            // updates its badge live when the result lands.
            if (_musicVideoMetadata is not null && detail.Tracks.Count > 0)
            {
                _ = _musicVideoMetadata.ApplyAvailabilityToAsync(
                    detail.Tracks,
                    static t => t.Uri,
                    static (t, v) => t.HasLinkedLocalVideo = v,
                    CancellationToken.None);
            }

            // Apply the real track snapshot in one reset. The grid-level loading
            // skeleton avoids constructing placeholder TrackItems before this.
            ApplyFilterAndSort();

            // Related albums
            _moreByArtist.ReplaceWith(detail.MoreByArtist);
            HasMoreByArtist = MoreByArtist.Count > 0;
            HasNoRelatedAlbums = !HasMoreByArtist;

            // Alternate releases (deluxe / remaster / anniversary editions of THIS album)
            _alternateReleases.ReplaceWith(detail.AlternateReleases);
            OnPropertyChanged(nameof(AlternateReleases));
            HasAlternateReleases = AlternateReleases.Count > 0;

            // Merch (non-blocking, loaded after main content)
            _ = LoadMerchAsync(albumId);

            // Similar albums + artist context + music-video signal — non-blocking,
            // run after main detail render so the track table paints first.
            _ = LoadSimilarAlbumsAsync(albumId);
            var primaryArtistUri = detail.Artists.FirstOrDefault()?.Uri;
            if (!string.IsNullOrEmpty(primaryArtistUri))
                _ = LoadArtistContextAsync(albumId, primaryArtistUri);
            if (_allTracks.Count == 1)
                _ = LoadMusicVideoSignalAsync(albumId, _allTracks[0]);
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

        return Task.CompletedTask;
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
            IsUserQueued = false,
            AlbumName = AlbumName,
            AlbumUri = AlbumId,
            ArtistUri = string.IsNullOrEmpty(t.ArtistId) ? null : t.ArtistId,
            IsExplicit = t.IsExplicit,
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
            OnPropertyChanged(nameof(HasMerch));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load merch for {AlbumId}", albumUri);
        }
    }

    private async Task LoadSimilarAlbumsAsync(string albumId)
    {
        // Seed with the most-played track on the album; fall back to first track.
        var seedTrack = _allTracks
            .Where(t => t.IsLoaded && t.Data is AlbumTrackDto)
            .Select(t => (AlbumTrackDto)t.Data!)
            .OrderByDescending(t => t.PlayCount)
            .FirstOrDefault();

        if (seedTrack == null || string.IsNullOrEmpty(seedTrack.Uri)) return;

        try
        {
            var results = await Task.Run(async () =>
                await _albumService.GetSimilarAlbumsAsync(seedTrack.Uri));
            if (AlbumId != albumId) return; // stale
            _similarAlbums.ReplaceWith(results);
            HasSimilarAlbums = _similarAlbums.Count > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load similar albums for {AlbumId}", albumId);
        }
    }

    private async Task LoadArtistContextAsync(string albumId, string artistUri)
    {
        try
        {
            var context = await Task.Run(async () =>
                await _albumService.GetArtistContextAsync(artistUri));
            if (AlbumId != albumId) return; // stale
            ArtistBioExcerpt = context.BioExcerpt;
            _similarArtists.ReplaceWith(context.SimilarArtists);
            HasSimilarArtists = _similarArtists.Count > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load artist context for {ArtistUri}", artistUri);
        }
    }

    private async Task LoadMusicVideoSignalAsync(string albumId, LazyTrackItem track)
    {
        if (track?.Data is not AlbumTrackDto dto || string.IsNullOrEmpty(dto.Uri)) return;

        try
        {
            var videoUri = await Task.Run(async () =>
                await _albumService.GetMusicVideoUriAsync(dto.Uri));
            if (AlbumId != albumId) return; // stale
            MusicVideoUri = videoUri;
            HasMusicVideo = !string.IsNullOrEmpty(videoUri);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to fetch music-video signal for {TrackUri}", dto.Uri);
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
        _similarAlbums.Clear();
        _similarArtists.Clear();
    }
}
