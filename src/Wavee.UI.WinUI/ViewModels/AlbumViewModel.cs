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
using Wavee.AI.Tools;
using Wavee.Core.Data;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.AvatarStack;
using Wavee.UI.WinUI.Controls.TabBar;
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
public sealed record DiscGroupHeader(int Number, string TitleText);

/// <summary>
/// ViewModel for the Album detail page.
/// Album tracks are static after load — no reactive pipeline needed.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class AlbumViewModel : Wavee.UI.ViewModels.Helpers.TrackListViewModelBase, ITrackListViewModel, ITabBarItemContent, IDisposable
{
    private readonly IAlbumService _albumService;
    private readonly AlbumStore _albumStore;
    private CompositeDisposable? _subscriptions;
    private string? _appliedDetailFor;
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ITrackLikeService? _likeService;
    private readonly AlbumBioSummarizer? _albumBioSummarizer;
    private readonly AiCapabilities? _aiCapabilities;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private bool _disposed;

    // ── On-device "About this album" AI state ──────────────────────────────
    private CancellationTokenSource? _albumAiSummaryCts;
    private string? _albumAiSummaryText;
    private bool _isAlbumAiSummaryStreaming;
    private bool _isAlbumAiSummaryLoading;
    private bool _wasLastAlbumAiSummaryFromCache;
    private string? _albumAiSummaryUnavailableText;
    private bool _hideAlbumAiCardForWeakGrounding;
    private readonly ObservableCollection<AiGroundingSourceLink> _albumAiSummarySources = [];

    /// <summary>All loaded tracks (unfiltered). Null until loaded.</summary>
    private List<LazyTrackItem> _allTracks = [];
    private HashSet<string> _popularTrackIds = new(StringComparer.Ordinal);

    private string _albumId = "";
    private bool _isSaved;
    private bool _isLoading;
    private bool _isLoadingTracks;
    private bool _isArtistSummaryLoading;
    private bool _isSimilarArtistsLoading;
    private bool _isMoreByArtistLoading;
    private bool _hasError;
    private string? _errorMessage;

    private string _searchQuery = "";
    private AlbumSortColumn _currentSortColumn = AlbumSortColumn.TrackNumber;
    private bool _isSortDescending = false;
    private int _totalTracks;
    private string _totalDuration = "";
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

    /// <summary>"Recommended Playlists" — curated editorial playlists tied to
    /// this album, sourced from RECOMMENDED_PLAYLISTS extended-metadata and
    /// projected to partial DTOs via batched LIST_METADATA_V2.</summary>
    private readonly ObservableCollection<PlaylistDetailDto> _recommendedPlaylists = [];

    private readonly Services.IPlaylistMetadataPrefetcher? _playlistMetadataPrefetcher;

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
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[diag-album-vm] Album.set EQUAL — no change. " +
                    $"oldId={(_album?.Id ?? "<null>")} oldName={(_album?.Name ?? "<null>")} oldImage={(_album?.ImageUrl ?? "<null>")}");
                return;
            }

            var old = _album;
            _album = value;
            System.Diagnostics.Debug.WriteLine(
                $"[diag-album-vm] Album.set CHANGED " +
                $"oldId={(old?.Id ?? "<null>")} → newId={(value?.Id ?? "<null>")} | " +
                $"oldName={(old?.Name ?? "<null>")} → newName={(value?.Name ?? "<null>")} | " +
                $"oldImage={(old?.ImageUrl ?? "<null>")} → newImage={(value?.ImageUrl ?? "<null>")}");
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
        nameof(MetaInlineLine), nameof(Palette), nameof(HasArtistAboutCard),
        nameof(ShowArtistSummarySkeleton), nameof(ShowArtistSummaryCard)
    ];

    private void RaiseAlbumEnvelopeDependents()
    {
        // Drop the cached AllDistinctArtists view so the next x:Bind read after
        // the property-changed burst rebuilds against the new Album envelope.
        _allDistinctArtistsView = null;

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

    private ObservableCollection<AlbumArtistResult>? _allDistinctArtistsView;

    /// <summary>
    /// Every distinct artist on the album: billed artists first, then track-only
    /// contributors deduped by URI. Drives the artists Flyout opened from the
    /// avatar stack so users can navigate to featured guests not in the billing.
    /// </summary>
    /// <remarks>
    /// Exposed as <see cref="ObservableCollection{T}"/> rather than
    /// <c>IReadOnlyList&lt;T&gt;</c> because CsWinRT cannot project a raw
    /// <c>IReadOnlyList&lt;T&gt;</c> through the WinRT ABI as
    /// <c>IBindableVector</c>, which causes
    /// <c>ItemsControl.set_ItemsSource</c> to throw "Value does not fall within
    /// the expected range." The cached view is invalidated in
    /// <see cref="RaiseAlbumEnvelopeDependents"/> so x:Bind re-reads on every
    /// <see cref="Album"/> swap.
    /// </remarks>
    public ObservableCollection<AlbumArtistResult> AllDistinctArtists =>
        _allDistinctArtistsView ??= new ObservableCollection<AlbumArtistResult>(
            Album?.AllDistinctArtists ?? []);

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
            if (SetProperty(ref _isLoading, value))
                OnPropertyChanged(nameof(IsContentReady));
        }
    }

    /// <summary>
    /// Loading state specifically for tracks.
    /// </summary>
    public bool IsLoadingTracks
    {
        get => _isLoadingTracks;
        set
        {
            if (SetProperty(ref _isLoadingTracks, value))
                OnPropertyChanged(nameof(IsContentReady));
        }
    }

    /// <summary>
    /// True once both header metadata and tracks have hydrated. Drives the
    /// footer rail's shimmer→content crossfade so the about-artist card and
    /// related shelves don't appear floating below skeleton track rows.
    /// </summary>
    public bool IsContentReady => !_isLoading && !_isLoadingTracks;

    public bool IsArtistSummaryLoading
    {
        get => _isArtistSummaryLoading;
        private set
        {
            if (SetProperty(ref _isArtistSummaryLoading, value))
            {
                OnPropertyChanged(nameof(ShowArtistSummarySkeleton));
                OnPropertyChanged(nameof(ShowArtistSummaryCard));
            }
        }
    }

    public bool IsSimilarArtistsLoading
    {
        get => _isSimilarArtistsLoading;
        private set
        {
            if (SetProperty(ref _isSimilarArtistsLoading, value))
            {
                OnPropertyChanged(nameof(ShowSimilarArtistsSkeleton));
                OnPropertyChanged(nameof(ShowSimilarArtistsPanel));
            }
        }
    }

    public bool IsMoreByArtistLoading
    {
        get => _isMoreByArtistLoading;
        private set
        {
            if (SetProperty(ref _isMoreByArtistLoading, value))
            {
                OnPropertyChanged(nameof(ShowMoreByArtistSkeleton));
                OnPropertyChanged(nameof(ShowMoreByArtistPanel));
            }
        }
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

    // ── On-device "About this album" AI surface ────────────────────────────

    /// <summary>True when the AI card should render (hardware + region + opt-in
    /// + per-feature toggle all open). The page x:Load binds to this so the
    /// section evaporates entirely on non-AI machines.</summary>
    public bool IsAiAlbumCardVisible =>
        _aiCapabilities?.IsAlbumBioSummarizeEnabled == true && !_hideAlbumAiCardForWeakGrounding;

    /// <summary>True while the load runs (model warmup + grounding fetch +
    /// streaming generation). Drives the card's shimmer / "generating" chrome.</summary>
    public bool IsAlbumAiSummaryGenerating => _isAlbumAiSummaryLoading || _isAlbumAiSummaryStreaming;

    /// <summary>The streamed-then-final AI paragraph. Null until first token lands.</summary>
    public string? AlbumAiSummaryText
    {
        get => _albumAiSummaryText;
        private set => SetProperty(ref _albumAiSummaryText, value);
    }

    /// <summary>True while tokens are still arriving from Phi Silica. Used by
    /// the card to flash a caret / typewriter effect.</summary>
    public bool IsAlbumAiSummaryStreaming
    {
        get => _isAlbumAiSummaryStreaming;
        private set
        {
            if (SetProperty(ref _isAlbumAiSummaryStreaming, value))
                OnPropertyChanged(nameof(IsAlbumAiSummaryGenerating));
        }
    }

    /// <summary>True between the call kicking off and the first streamed token
    /// (or, when the result is cached, between kickoff and the cached-result
    /// flip). The card uses this to render its initial shimmer.</summary>
    public bool IsAlbumAiSummaryLoading
    {
        get => _isAlbumAiSummaryLoading;
        private set
        {
            if (SetProperty(ref _isAlbumAiSummaryLoading, value))
                OnPropertyChanged(nameof(IsAlbumAiSummaryGenerating));
        }
    }

    /// <summary>True when the bound text was served from the per-album cache
    /// (no fresh model run). The card uses this to skip the reveal animation.</summary>
    public bool WasLastAlbumAiSummaryFromCache
    {
        get => _wasLastAlbumAiSummaryFromCache;
        private set => SetProperty(ref _wasLastAlbumAiSummaryFromCache, value);
    }

    /// <summary>One-line "why no AI text" message bound to the empty-state
    /// placeholder. Null while a run is in flight or successful.</summary>
    public string? AlbumAiSummaryUnavailableText
    {
        get => _albumAiSummaryUnavailableText;
        private set => SetProperty(ref _albumAiSummaryUnavailableText, value);
    }

    public IReadOnlyList<AiGroundingSourceLink> AlbumAiSummarySources => _albumAiSummarySources;

    public bool HasAlbumAiSummarySources => _albumAiSummarySources.Count > 0;

    /// <summary>
    /// Refresh selection-driven command CanExecute flags when SelectedItems
    /// changes. The base class hooks the partial method on SelectedItems and
    /// calls this; the property itself is inherited.
    /// </summary>
    protected override void OnSelectionChanged()
    {
        PlaySelectedCommand.NotifyCanExecuteChanged();
        PlayAfterCommand.NotifyCanExecuteChanged();
        AddSelectedToQueueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// More albums by the same artist.
    /// </summary>
    public IReadOnlyList<AlbumRelatedResult> MoreByArtist => _moreByArtist;

    private bool _hasMoreByArtist;
    public bool HasMoreByArtist
    {
        get => _hasMoreByArtist;
        private set
        {
            if (SetProperty(ref _hasMoreByArtist, value))
                OnPropertyChanged(nameof(ShowMoreByArtistPanel));
        }
    }

    public bool ShowMoreByArtistSkeleton => IsMoreByArtistLoading;
    public bool ShowMoreByArtistPanel => !IsMoreByArtistLoading && HasMoreByArtist;

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

    /// <summary>
    /// Curated playlists Spotify recommends for this album. Sourced from
    /// RECOMMENDED_PLAYLISTS extended-metadata; each item is a partial
    /// PlaylistDetailDto carrying name + cover + (when editorial) header URL.
    /// </summary>
    public IReadOnlyList<PlaylistDetailDto> RecommendedPlaylists => _recommendedPlaylists;

    private bool _hasRecommendedPlaylists;
    public bool HasRecommendedPlaylists
    {
        get => _hasRecommendedPlaylists;
        private set => SetProperty(ref _hasRecommendedPlaylists, value);
    }

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
        private set
        {
            if (SetProperty(ref _hasSimilarArtists, value))
                OnPropertyChanged(nameof(ShowSimilarArtistsPanel));
        }
    }

    public bool ShowSimilarArtistsSkeleton => IsSimilarArtistsLoading;
    public bool ShowSimilarArtistsPanel => !IsSimilarArtistsLoading && HasSimilarArtists;

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

    public string? ArtistMonthlyListenersDisplay =>
        ArtistMonthlyListeners > 0
            ? $"{ArtistMonthlyListeners:N0} monthly listeners"
            : null;

    public IReadOnlyList<string> ArtistSummaryTopTrackNames => _allTracks
        .Where(t => t.IsLoaded && t.Data is AlbumTrackDto)
        .Select(t => ((AlbumTrackDto)t.Data!).Title)
        .Where(title => !string.IsNullOrWhiteSpace(title))
        .Take(5)
        .ToArray();

    // ── "About the artist" card (NPV-sourced) ────────────────────────────
    //
    // Populated by LoadArtistNpvAsync on every album open. Avatar / verified /
    // monthly-listeners come straight from the queryNpvArtist response;
    // the bio excerpt overlays the existing ArtistBioExcerpt (NPV wins when
    // present, which matters for short releases where the heavier
    // queryArtistOverview path is skipped).

    private string? _artistAvatarImageUrl;
    public string? ArtistAvatarImageUrl
    {
        get => _artistAvatarImageUrl;
        private set => SetProperty(ref _artistAvatarImageUrl, value);
    }

    private bool _isArtistVerified;
    public bool IsArtistVerified
    {
        get => _isArtistVerified;
        private set => SetProperty(ref _isArtistVerified, value);
    }

    private long _artistMonthlyListeners;
    public long ArtistMonthlyListeners
    {
        get => _artistMonthlyListeners;
        private set
        {
            if (SetProperty(ref _artistMonthlyListeners, value))
                OnPropertyChanged(nameof(ArtistMonthlyListenersDisplay));
        }
    }

    /// <summary>True when the local <c>ITrackLikeService</c> reports this
    /// album's lead artist as followed. Mirrors <c>ArtistViewModel.IsFollowing</c>'s
    /// pattern — kept in sync via the existing <c>SaveStateChanged</c>
    /// subscription that the album-save heart button already uses.</summary>
    private bool _isArtistFollowing;
    public bool IsArtistFollowing
    {
        get => _isArtistFollowing;
        set => SetProperty(ref _isArtistFollowing, value);
    }

    /// <summary>True when the "About the artist" card has enough data to
    /// render — name is always set from album detail, so the only gate is
    /// having a valid <see cref="ArtistId"/>. Avatar / bio gracefully fall
    /// back to placeholders inside the card when missing.</summary>
    public bool HasArtistAboutCard => !string.IsNullOrEmpty(ArtistId) && !string.IsNullOrEmpty(ArtistName);
    public bool ShowArtistSummarySkeleton => IsArtistSummaryLoading && HasArtistAboutCard;
    public bool ShowArtistSummaryCard => !IsArtistSummaryLoading && HasArtistAboutCard;

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

    /// <summary>"ALBUM" / "SINGLE" / "EP" / "COMPILATION", localized + upper-cased for the hero pill.</summary>
    public string AlbumTypeUpper => AlbumType?.ToLowerInvariant() switch
    {
        "single" => AppLocalization.GetString("AlbumType_Single"),
        "ep" => AppLocalization.GetString("AlbumType_EP"),
        "compilation" => AppLocalization.GetString("AlbumType_Compilation"),
        "soundtrack" => AppLocalization.GetString("AlbumType_Soundtrack"),
        _ => AppLocalization.GetString("AlbumType_Album"),
    };

    /// <summary>
    /// Filtered and sorted tracks for UI binding. Stable instance — mutate via
    /// <see cref="ObservableCollectionExtensions.ReplaceWith{T}"/> / Clear so the
    /// bound ListView keeps its CollectionChanged subscription across navs.
    /// </summary>
    private readonly ObservableCollection<LazyTrackItem> _filteredTracks = [];
    public IReadOnlyList<LazyTrackItem> FilteredTracks => _filteredTracks;

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
    /// Builds the <see cref="DiscGroupHeader"/> payload for one disc.
    /// Called once per group with the group's first item.
    /// </summary>
    public Func<ITrackItem, object> DiscGroupHeaderSelector { get; }

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
        ILogger<AlbumViewModel>? logger = null,
        Services.IPlaylistMetadataPrefetcher? playlistMetadataPrefetcher = null,
        AlbumBioSummarizer? albumBioSummarizer = null,
        AiCapabilities? aiCapabilities = null)
    {
        _albumService = albumService;
        _albumStore = albumStore;
        _libraryDataService = libraryDataService;
        _playbackStateService = playbackStateService;
        _likeService = likeService;
        _musicVideoMetadata = musicVideoMetadata;
        _playlistMetadataPrefetcher = playlistMetadataPrefetcher;
        _albumBioSummarizer = albumBioSummarizer;
        _aiCapabilities = aiCapabilities;
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
            return new DiscGroupHeader(
                disc,
                $"Disc {disc.ToString(CultureInfo.InvariantCulture)}");
        };

        AttachLongLivedServices();

        Diagnostics.LiveInstanceTracker.Register(this);
    }

    // Long-lived singleton subscriptions are attached lazily and detached on
    // Deactivate/Dispose so the (Transient) VM is not pinned by singleton invocation lists.
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

    private bool IsCurrentAlbum(string albumId)
        => !_disposed && string.Equals(AlbumId, albumId, StringComparison.Ordinal);

    private Task RunOnUiThreadAsync(Action action)
    {
        bool hasThreadAccess;
        try
        {
            hasThreadAccess = _dispatcherQueue.HasThreadAccess;
        }
        catch
        {
            return Task.CompletedTask;
        }

        if (hasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetResult(null);
            }
        }
        catch
        {
            tcs.TrySetResult(null);
        }

        return tcs.Task;
    }

    private Task RunCurrentAlbumUiAsync(string albumId, Action action)
        => RunOnUiThreadAsync(() =>
        {
            if (IsCurrentAlbum(albumId))
                action();
        });

    public void Initialize(string albumId, bool preserveHeaderPrefill = false)
    {
        AttachLongLivedServices();
        var branch = AlbumId != albumId ? "reset" : "same";
        var imageBefore = AlbumImageUrl ?? "<null>";
        var nameBefore = AlbumName ?? "<null>";
        System.Diagnostics.Debug.WriteLine(
            $"[diag-album-vm] Initialize.enter incoming={albumId} current={AlbumId} branch={branch} " +
            $"preserveHeaderPrefill={preserveHeaderPrefill} imageBefore={imageBefore} nameBefore={nameBefore}");
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
                    // TotalTracks intentionally NOT reset here — when
                    // preserveHeaderPrefill=true the caller has already run
                    // PrefillFrom(nav) which seeded the envelope's TotalTracks
                    // from the navigation source. Wiping it would force the
                    // skeleton back to its default row count for the brief
                    // window between Initialize and ApplyDetailAsync, defeating
                    // the prefill.
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
            OnPropertyChanged(nameof(ArtistSummaryTopTrackNames));
            // Mirror Album.TotalTracks onto the property so TrackDataGrid's
            // LoadingRowCount binding sees the right value. With prefill the
            // envelope holds the nav-supplied count; without prefill the freshly
            // constructed envelope's TotalTracks defaults to 0 (→ skeleton falls
            // back to DefaultLoadingRowCount).
            TotalTracks = Album?.TotalTracks ?? 0;
            RebuildDiscMetadata();
            _popularTrackIds.Clear();
            if (_filteredTracks.Count > 0)
                _filteredTracks.Clear();
        }

        AlbumId = albumId;
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Album, albumId)
        {
            Title = AppLocalization.GetString("TabTitle_Album")
        };
        if (preserveHeaderPrefill)
            UpdateTabTitle();

        RefreshSaveState();
        System.Diagnostics.Debug.WriteLine(
            $"[diag-album-vm] Initialize.exit AlbumId={AlbumId} AlbumName={(AlbumName ?? "<null>")} " +
            $"AlbumImageUrl={(AlbumImageUrl ?? "<null>")} IsLoading={IsLoading}");
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

    private void BeginSecondaryAlbumSectionLoading()
    {
        IsArtistSummaryLoading = true;
        IsSimilarArtistsLoading = true;
        IsMoreByArtistLoading = true;
    }

    private void EndSecondaryAlbumSectionLoading()
    {
        IsArtistSummaryLoading = false;
        IsSimilarArtistsLoading = false;
        IsMoreByArtistLoading = false;
    }

    private void ClearSecondaryAlbumSections()
    {
        BeginSecondaryAlbumSectionLoading();

        _alternateReleases.ClearWithoutNotify();
        OnPropertyChanged(nameof(AlternateReleases));
        HasAlternateReleases = false;

        _moreByArtist.ClearWithoutNotify();
        HasMoreByArtist = false;
        HasNoRelatedAlbums = false;

        _merchItems.ClearWithoutNotify();
        OnPropertyChanged(nameof(HasMerch));

        _similarAlbums.ClearWithoutNotify();
        HasSimilarAlbums = false;

        _similarArtists.ClearWithoutNotify();
        HasSimilarArtists = false;
        ArtistBioExcerpt = null;
        ArtistAvatarImageUrl = null;
        IsArtistVerified = false;
        ArtistMonthlyListeners = 0;
        IsArtistFollowing = false;

        _recommendedPlaylists.ClearWithoutNotify();
        HasRecommendedPlaylists = false;

        HasMusicVideo = false;
        MusicVideoUri = null;

        // Clear AI summary state for the new album so the page doesn't briefly
        // paint stale text from the previous album while the new generation
        // spins up.
        try { _albumAiSummaryCts?.Cancel(); } catch { /* already disposed */ }
        _albumAiSummaryCts = null;
        AlbumAiSummaryText = null;
        WasLastAlbumAiSummaryFromCache = false;
        IsAlbumAiSummaryStreaming = false;
        IsAlbumAiSummaryLoading = false;
        AlbumAiSummaryUnavailableText = null;
        _hideAlbumAiCardForWeakGrounding = false;
        ClearAlbumAiSummarySources();
        OnPropertyChanged(nameof(IsAiAlbumCardVisible));
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
    /// Recomputes <see cref="IsMultiDisc"/>. Call after <c>_allTracks</c> is replaced.
    /// </summary>
    private void RebuildDiscMetadata()
    {
        if (_allTracks.Count == 0)
        {
            IsMultiDisc = false;
            return;
        }

        var discs = new HashSet<int>();
        foreach (var t in _allTracks)
        {
            var disc = (t.Data as AlbumTrackDto)?.DiscNumber ?? 1;
            discs.Add(disc);
            if (discs.Count > 1)
            {
                IsMultiDisc = true;
                return;
            }
        }

        IsMultiDisc = false;
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

    private static string FormatCopyright(AlbumCopyrightResult c)
    {
        var prefix = c.Type == "P" ? "℗" : "©";
        var text = c.Text?.TrimStart() ?? "";
        if (text.StartsWith("©") || text.StartsWith("℗"))
            return text;
        return $"{prefix} {text}";
    }

    // Pure data-prep for ApplyDetailAsync — runs on a background thread so the
    // nav frame can paint before the artist-graph LINQ + track materialisation
    // finishes. No DPs, no ObservableCollections, no logger access in here.
    private static AlbumDetailPrep BuildDetailPrep(AlbumDetailResult detail)
    {
        var tracks = detail.Tracks
            .Select((t, i) => LazyTrackItem.Loaded(t.Id, i + 1, t))
            .ToList();
        var totalSeconds = tracks.Sum(t => t.Duration.TotalSeconds);
        var popularTrackIds = BuildPopularTrackIdSet(detail.Tracks);

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

        int? year = detail.ReleaseDate.Year > 0 ? detail.ReleaseDate.Year : null;
        string? releaseDateFormatted = detail.ReleaseDate != default
            ? detail.ReleaseDate.ToString("MMMM d, yyyy")
            : null;
        string? copyrightsText = detail.Copyrights?.Count > 0
            ? string.Join("\n", detail.Copyrights.Select(FormatCopyright))
            : null;
        var (preReleaseFormatted, preReleaseRelative) =
            FormatPreRelease(detail.IsPreRelease, detail.PreReleaseEndDateTime);

        return new AlbumDetailPrep(
            tracks, totalSeconds, popularTrackIds,
            billed, avatarItems, headerArtistLinks, allDistinctArtists, trackExtras.Count,
            year, releaseDateFormatted, copyrightsText,
            preReleaseFormatted, preReleaseRelative);
    }

    private sealed record AlbumDetailPrep(
        List<LazyTrackItem> Tracks,
        double TotalSeconds,
        HashSet<string> PopularTrackIds,
        IReadOnlyList<AlbumArtistResult> Billed,
        IReadOnlyList<AvatarStackItem> AvatarItems,
        IReadOnlyList<HeaderArtistLink> HeaderArtistLinks,
        IReadOnlyList<AlbumArtistResult> AllDistinctArtists,
        int OverflowArtistCount,
        int? Year,
        string? ReleaseDateFormatted,
        string? CopyrightsText,
        string? PreReleaseFormatted,
        string? PreReleaseRelative);

    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
            return AppLocalization.Format("Duration_HoursMinutes", (int)ts.TotalHours, ts.Minutes);
        return AppLocalization.Format("Duration_Minutes", ts.Minutes);
    }

    /// <summary>"12 songs · 38 min · 1980" — null parts are skipped.</summary>
    private static string BuildMetaInlineLine(int trackCount, double totalSeconds, int year)
    {
        var parts = new List<string>(3);
        if (trackCount > 0) parts.Add(trackCount == 1
            ? AppLocalization.GetString("Count_Song_One")
            : AppLocalization.Format("Count_Song_Many", trackCount));
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
        var formatted = AppLocalization.Format("Album_PreRelease_Coming", local.ToString("dddd, MMMM d"), local.ToString("HH:mm"));

        var delta = local - DateTimeOffset.Now;
        string relative;
        if (delta.TotalDays >= 1)
        {
            var days = (int)Math.Ceiling(delta.TotalDays);
            relative = days == 1
                ? AppLocalization.GetString("Relative_InOneDay")
                : AppLocalization.Format("Relative_InDays", days);
        }
        else if (delta.TotalHours >= 1)
        {
            var hours = (int)Math.Ceiling(delta.TotalHours);
            relative = hours == 1
                ? AppLocalization.GetString("Relative_InOneHour")
                : AppLocalization.Format("Relative_InHours", hours);
        }
        else
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(delta.TotalMinutes));
            relative = minutes == 1
                ? AppLocalization.GetString("Relative_InOneMinute")
                : AppLocalization.Format("Relative_InMinutes", minutes);
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
        var prefillTracks = nav.TotalTracks ?? 0;
        System.Diagnostics.Debug.WriteLine(
            $"[diag-album-vm] PrefillFrom.enter clearMissing={clearMissing} " +
            $"nav.Uri={nav.Uri} nav.Title={(nav.Title ?? "<null>")} nav.ImageUrl={(nav.ImageUrl ?? "<null>")} " +
            $"nav.Subtitle={(nav.Subtitle ?? "<null>")} nav.TotalTracks={nav.TotalTracks} " +
            $"current.Name={(current.Name ?? "<null>")} current.ImageUrl={(current.ImageUrl ?? "<null>")}");
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
                : clearMissing ? string.Empty : current.ArtistName,
            TotalTracks = prefillTracks > 0
                ? prefillTracks
                : clearMissing ? 0 : current.TotalTracks
        };
        if (prefillTracks > 0)
            TotalTracks = prefillTracks;
        else if (clearMissing)
            TotalTracks = 0;
    }

    /// <summary>
    /// Apply a viewport-prefetched AlbumV4 partial. Paints the hero (cover /
    /// title / artists / year / type) and sets <see cref="TotalTracks"/> so
    /// <c>TrackDataGrid.LoadingRowCount</c> renders the correct-count skeleton.
    /// Leaves <see cref="IsLoadingTracks"/> at true and skips every secondary
    /// fetch (rootlist / merch / similar / artist-context / music-video /
    /// alternate-releases / more-by-artist); those run on the full pass when
    /// the Pathfinder payload arrives.
    /// </summary>
    private Task ApplyPartialDetailAsync(AlbumDetailResult detail, string albumId)
    {
        try
        {
            TotalTracks = detail.TotalTracks;

            var current = Album ?? EmptyAlbumEnvelope();
            var billed = detail.Artists ?? [];
            var firstArtist = billed.FirstOrDefault();
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
            var year = detail.ReleaseDate.Year > 0 ? detail.ReleaseDate.Year : current.Year;

            Album = current with
            {
                Id = albumId,
                Name = !string.IsNullOrEmpty(detail.Name) ? detail.Name : current.Name,
                ImageUrl = !string.IsNullOrEmpty(detail.CoverArtUrl) ? detail.CoverArtUrl : current.ImageUrl,
                ArtistId = !string.IsNullOrEmpty(firstArtist?.Uri) ? firstArtist!.Uri! : current.ArtistId,
                ArtistName = !string.IsNullOrEmpty(firstArtist?.Name) ? firstArtist!.Name! : current.ArtistName,
                Artists = billed,
                ArtistAvatarItems = avatarItems,
                HeaderArtistLinks = headerArtistLinks,
                AllDistinctArtists = billed,
                Year = year,
                Type = !string.IsNullOrEmpty(detail.Type) ? detail.Type : current.Type,
                TotalTracks = detail.TotalTracks,
            };

            IsLoading = false;
            // IsLoadingTracks intentionally NOT touched — the track grid keeps
            // showing its skeleton (now sized to exactly TotalTracks rows) until
            // the full Pathfinder payload lands and the full branch below sets
            // IsLoadingTracks=false.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ApplyPartialDetailAsync failed for {AlbumId}", XfadeLog.Tag(albumId));
        }
        return Task.CompletedTask;
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

        // Viewport-prefetched AlbumV4 partial: paint hero + correct skeleton
        // row count, but the track list itself + all secondary sections must
        // wait for the authoritative Pathfinder fetch (already scheduled by
        // MaterializeAsync because Hint emitted Ready+Stale). When the full
        // payload arrives, ApplyDetailState re-invokes ApplyDetailAsync with
        // IsPartial=false and the full branch below runs as today.
        if (detail.IsPartial)
        {
            await ApplyPartialDetailAsync(detail, albumId);
            return;
        }

        try
        {
            // Push the heavy LINQ (track wrapping, artist-graph filter+group,
            // copyright join, formatted strings) to the threadpool so the nav
            // frame can paint before this method's UI-thread tail runs.
            // Mirrors ArtistViewModel.LoadAsync's deferred-hydration pattern;
            // our caller already invokes us as `_ = ApplyDetailAsync`.
            var prep = await Task.Run(() => BuildDetailPrep(detail)).ConfigureAwait(false);

            // The user may have navigated to a different album while the prep
            // ran on the threadpool. Bail before touching observables.
            if (!IsCurrentAlbum(albumId)) return;

            await RunCurrentAlbumUiAsync(albumId, () =>
            {
                _allTracks = prep.Tracks;
                OnPropertyChanged(nameof(ArtistSummaryTopTrackNames));
                RebuildDiscMetadata();
                _popularTrackIds = prep.PopularTrackIds;

                TotalTracks = _allTracks.Count;
                TotalDuration = FormatDuration(prep.TotalSeconds);
                IsLoadingTracks = false;

                var current = Album ?? EmptyAlbumEnvelope();
                var firstArtist = detail.Artists.FirstOrDefault();
                var year = prep.Year ?? current.Year;
                var releaseDateFormatted = prep.ReleaseDateFormatted ?? current.ReleaseDateFormatted;
                var copyrightsText = prep.CopyrightsText ?? current.CopyrightsText;

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
                    Artists = prep.Billed,
                    ArtistAvatarItems = prep.AvatarItems,
                    HeaderArtistLinks = prep.HeaderArtistLinks,
                    AllDistinctArtists = prep.AllDistinctArtists,
                    OverflowArtistCount = prep.OverflowArtistCount,
                    Year = year,
                    Type = !string.IsNullOrEmpty(detail.Type) ? detail.Type : current.Type,
                    Label = detail.Label,
                    ReleaseDateFormatted = releaseDateFormatted,
                    CopyrightsText = copyrightsText,
                    IsPreRelease = detail.IsPreRelease,
                    PreReleaseEndDateTime = detail.PreReleaseEndDateTime,
                    PreReleaseFormatted = prep.PreReleaseFormatted,
                    PreReleaseRelative = prep.PreReleaseRelative,
                    ShareUrl = detail.ShareUrl,
                    MetaInlineLine = BuildMetaInlineLine(TotalTracks, prep.TotalSeconds, year),
                    Palette = detail.Palette,
                    TotalTracks = TotalTracks
                };

                IsSaved = detail.IsSaved;
                ArtistAvatarImageUrl = Album.ArtistImageUrl;
                RefreshSaveState();
                IsLoading = false;

                // Apply the real track snapshot in one reset. The grid-level loading
                // skeleton avoids constructing placeholder TrackItems before this.
                ApplyFilterAndSort();

                BeginSecondaryAlbumSectionLoading();
                _ = ApplySecondaryAlbumSectionsAsync(detail, albumId);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunCurrentAlbumUiAsync(albumId, () =>
            {
                HasError = true;
                ErrorMessage = ex.Message;
            }).ConfigureAwait(false);
            _logger?.LogError(ex, "Failed to load album {AlbumId}", albumId);
        }
        finally
        {
            await RunCurrentAlbumUiAsync(albumId, () => IsLoading = false).ConfigureAwait(false);
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

    /// <summary>
    /// Kicks off the on-device "About this album" Phi Silica generation. Cancels
    /// any prior run so a fast album-to-album nav doesn't leak an in-flight
    /// model call into the new page. Streams tokens into <see cref="AlbumAiSummaryText"/>;
    /// failures collapse the card to an unavailable empty-state.
    /// </summary>
    private async Task LoadAlbumAiSummaryAsync(string albumId)
    {
        if (_albumBioSummarizer is null || _aiCapabilities is null)
            return;
        if (!_aiCapabilities.IsAlbumBioSummarizeEnabled)
            return;
        if (string.IsNullOrEmpty(albumId))
            return;

        var albumTitle = Album?.Name;
        if (string.IsNullOrWhiteSpace(albumTitle))
            return;

        _albumAiSummaryCts?.Cancel();
        var cts = _albumAiSummaryCts = new CancellationTokenSource();

        var trackTitles = _allTracks
            .Where(t => t is { IsLoaded: true, Data: AlbumTrackDto d } && !string.IsNullOrWhiteSpace(d.Title))
            .Select(t => ((AlbumTrackDto)t.Data!).Title!)
            .Take(30)
            .ToList();

        try
        {
            IsAlbumAiSummaryLoading = true;
            AlbumAiSummaryText = null;
            WasLastAlbumAiSummaryFromCache = false;
            IsAlbumAiSummaryStreaming = false;
            AlbumAiSummaryUnavailableText = null;
            _hideAlbumAiCardForWeakGrounding = false;
            ClearAlbumAiSummarySources();
            OnPropertyChanged(nameof(IsAiAlbumCardVisible));

            var streamedText = string.Empty;
            var streamCompleted = 0;
            var progress = new Progress<string>(delta =>
            {
                if (cts.IsCancellationRequested
                    || Interlocked.CompareExchange(ref streamCompleted, 0, 0) != 0)
                {
                    return;
                }

                streamedText += delta;
                var preview = streamedText.TrimStart();
                if (string.IsNullOrWhiteSpace(preview))
                    return;

                RunOnUiThreadAsync(() =>
                {
                    if (cts.IsCancellationRequested
                        || Interlocked.CompareExchange(ref streamCompleted, 0, 0) != 0)
                    {
                        return;
                    }

                    if (AiGeneratedTextGuard.IsInvalidGeneratedText(preview))
                    {
                        IsAlbumAiSummaryStreaming = false;
                        AlbumAiSummaryText = null;
                        return;
                    }

                    IsAlbumAiSummaryStreaming = true;
                    AlbumAiSummaryText = preview;
                });
            });

            var artistName = Album?.ArtistName;
            var year = Album?.Year is int yr && yr > 0 ? yr : (int?)null;
            var label = Album?.Label;
            var totalDurationDisplay = string.IsNullOrWhiteSpace(TotalDuration) ? null : TotalDuration;

            var result = await _albumBioSummarizer.SummarizeAlbumAsync(
                albumId,
                albumTitle!,
                artistName: artistName,
                releaseYear: year,
                trackTitles: trackTitles,
                label: label,
                totalDurationDisplay: totalDurationDisplay,
                deltaProgress: progress,
                ct: cts.Token).ConfigureAwait(false);

            if (cts.IsCancellationRequested || !IsCurrentAlbum(albumId)) return;
            Interlocked.Exchange(ref streamCompleted, 1);

            await RunOnUiThreadAsync(() =>
            {
                if (cts.IsCancellationRequested || !IsCurrentAlbum(albumId)) return;

                IsAlbumAiSummaryLoading = false;
                if (result.Kind == LyricsAiResultKind.Ok)
                {
                    WasLastAlbumAiSummaryFromCache = result.FromCache;
                    AlbumAiSummaryText = result.Text;
                    IsAlbumAiSummaryStreaming = false;
                    SetAlbumAiSummarySources(result.Sources);
                }
                else
                {
                    IsAlbumAiSummaryStreaming = false;
                    ClearAlbumAiSummarySources();
                    if (string.Equals(result.ErrorMessage, "insufficient_grounding", StringComparison.Ordinal)
                        || string.Equals(result.ErrorMessage, "invalid_generation", StringComparison.Ordinal))
                    {
                        _hideAlbumAiCardForWeakGrounding = true;
                        AlbumAiSummaryUnavailableText = null;
                        OnPropertyChanged(nameof(IsAiAlbumCardVisible));
                        return;
                    }

                    AlbumAiSummaryUnavailableText = result.Kind switch
                    {
                        LyricsAiResultKind.Filtered => "The on-device model could not describe this album safely.",
                        LyricsAiResultKind.Unavailable => "On-device AI is not available for this album right now.",
                        LyricsAiResultKind.Empty => "There is not enough album context to generate a description yet.",
                        _ => "The on-device model could not describe this album right now.",
                    };
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* album switched */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadAlbumAiSummaryAsync failed for {AlbumId}", albumId);
            if (!cts.IsCancellationRequested)
            {
                await RunOnUiThreadAsync(() =>
                {
                    IsAlbumAiSummaryLoading = false;
                    IsAlbumAiSummaryStreaming = false;
                    ClearAlbumAiSummarySources();
                    AlbumAiSummaryUnavailableText = "The on-device model could not describe this album right now.";
                }).ConfigureAwait(false);
            }
        }
    }

    private void SetAlbumAiSummarySources(IReadOnlyList<MusicGroundingSource>? sources)
    {
        _albumAiSummarySources.Clear();
        if (sources is not null)
        {
            foreach (var source in sources.Take(4))
            {
                if (string.IsNullOrWhiteSpace(source.Url)
                    || !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(source.SourceName)
                    ? uri.Host
                    : source.SourceName;
                _albumAiSummarySources.Add(new AiGroundingSourceLink(label, uri));
            }
        }

        OnPropertyChanged(nameof(AlbumAiSummarySources));
        OnPropertyChanged(nameof(HasAlbumAiSummarySources));
    }

    private void ClearAlbumAiSummarySources()
    {
        if (_albumAiSummarySources.Count == 0)
            return;

        _albumAiSummarySources.Clear();
        OnPropertyChanged(nameof(AlbumAiSummarySources));
        OnPropertyChanged(nameof(HasAlbumAiSummarySources));
    }

    private async Task ApplySecondaryAlbumSectionsAsync(AlbumDetailResult detail, string albumId)
    {
        try
        {
            // Let hero + track grid paint before shelves, artist context, playlist
            // rootlist, and video-badge probes invalidate more UI.
            await Task.Delay(32).ConfigureAwait(false);
            if (!IsCurrentAlbum(albumId)) return;

            await RunCurrentAlbumUiAsync(albumId, () =>
            {
                _moreByArtist.ReplaceWith(detail.MoreByArtist);
                HasMoreByArtist = MoreByArtist.Count > 0;
                HasNoRelatedAlbums = !HasMoreByArtist;
                IsMoreByArtistLoading = false;
            }).ConfigureAwait(false);

            await Task.Yield();
            if (!IsCurrentAlbum(albumId)) return;

            await RunCurrentAlbumUiAsync(albumId, () =>
            {
                _alternateReleases.ReplaceWith(detail.AlternateReleases);
                OnPropertyChanged(nameof(AlternateReleases));
                HasAlternateReleases = AlternateReleases.Count > 0;
            }).ConfigureAwait(false);

            await Task.Yield();
            if (!IsCurrentAlbum(albumId)) return;

            await RunCurrentAlbumUiAsync(albumId, () =>
            {
                if (_musicVideoMetadata is not null && detail.Tracks.Count > 0)
                    _ = ApplyMusicVideoAvailabilityAsync(detail.Tracks);

                _ = LoadMerchAsync(albumId);
                _ = LoadSimilarAlbumsAsync(albumId);

                if (_allTracks.Count is >= 1 and <= 2)
                {
                    _ = LoadSingleTrackContextAsync(albumId, _allTracks[0]);
                }
                else
                {
                    var multiTrackArtistUri = detail.Artists.FirstOrDefault()?.Uri;
                    if (!string.IsNullOrEmpty(multiTrackArtistUri))
                        _ = LoadArtistContextAsync(albumId, multiTrackArtistUri);
                    else
                        IsSimilarArtistsLoading = false;
                }

                var npvArtistUri = detail.Artists.FirstOrDefault()?.Uri;
                var npvLeadTrackUri = (_allTracks.FirstOrDefault()?.Data as AlbumTrackDto)?.Uri;
                if (!string.IsNullOrEmpty(npvArtistUri) && !string.IsNullOrEmpty(npvLeadTrackUri))
                    _ = LoadArtistNpvAsync(albumId, npvArtistUri, npvLeadTrackUri);
                else
                    IsArtistSummaryLoading = false;

                RefreshArtistFollowState();
                _ = LoadRecommendedPlaylistsAsync(albumId);

                // Kick the on-device "About this album" excerpt. Gated through
                // AiCapabilities inside the summarizer so non-Copilot+ PCs or
                // disabled AI stay a cheap no-op.
                _ = LoadAlbumAiSummaryAsync(albumId);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunCurrentAlbumUiAsync(albumId, EndSecondaryAlbumSectionLoading).ConfigureAwait(false);
            _logger?.LogDebug(ex, "ApplySecondaryAlbumSectionsAsync failed for {AlbumId}", albumId);
        }
    }

    private async Task ApplyMusicVideoAvailabilityAsync(IReadOnlyList<AlbumTrackDto> tracks)
    {
        try
        {
            if (_musicVideoMetadata is null || tracks.Count == 0)
                return;

            await _musicVideoMetadata.ApplyAvailabilityToAsync(
                tracks,
                static t => t.Uri,
                static (t, v) => t.HasLinkedLocalVideo = v,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Music-video availability probe failed for album {AlbumId}", AlbumId);
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
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
                return;

            RefreshSaveState();
            // Same broadcast covers the album-heart AND the artist-follow pill
            // — both bind to ITrackLikeService state, so a toggle from any
            // other surface should re-paint the AlbumPage glyphs.
            RefreshArtistFollowState();
        });
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
        => BuildQueueAndPlay(FilteredTracks, startIndex, shuffle);

    private void BuildQueueAndPlay(IEnumerable<LazyTrackItem> source, int startIndex, bool shuffle)
    {
        var queueItems = source.Select(t => new QueueItem
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
            HasVideo = t.HasVideo,
        }).ToList();

        if (queueItems.Count == 0) return;

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

    private IReadOnlyList<string> CollectSelectedTrackUris()
        => SelectedItems
            .OfType<ITrackItem>()
            .Select(t => t.Uri)
            .Where(u => !string.IsNullOrEmpty(u))
            .ToArray();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlaySelected()
    {
        var selected = SelectedItems.OfType<LazyTrackItem>().ToList();
        if (selected.Count == 0) return;
        BuildQueueAndPlay(selected, startIndex: 0, shuffle: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlayAfter()
    {
        var uris = CollectSelectedTrackUris();
        if (uris.Count == 0) return;
        _playbackStateService.PlayNext(uris);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddSelectedToQueue()
    {
        var uris = CollectSelectedTrackUris();
        if (uris.Count == 0) return;
        _playbackStateService.AddToQueue(uris);
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        // Not applicable for albums - tracks can't be removed
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
            var items = await Task.Run(async () => await _albumService.GetMerchAsync(albumUri))
                .ConfigureAwait(false);

            await RunCurrentAlbumUiAsync(albumUri, () =>
            {
                _merchItems.ReplaceWith(items);
                OnPropertyChanged(nameof(HasMerch));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load merch for {AlbumId}", albumUri);
        }
    }

    private async Task LoadRecommendedPlaylistsAsync(string albumUri)
    {
        try
        {
            var items = await Task.Run(() => _albumService.GetRecommendedPlaylistsAsync(albumUri))
                .ConfigureAwait(false);

            // Staleness guard — the user may have navigated to another album
            // while the batched extended-metadata POST was in flight.
            if (!IsCurrentAlbum(albumUri)) return;

            await RunCurrentAlbumUiAsync(albumUri, () =>
            {
                _recommendedPlaylists.ReplaceWith(items);
                HasRecommendedPlaylists = items.Count > 0;
            }).ConfigureAwait(false);

            // Warm PlaylistStore + BitmapImage cache so clicking a recommended
            // card opens the playlist page with the hero already populated.
            // The prefetcher's session-wide `_alreadyKicked` guard makes repeat
            // enqueues free; the underlying ExtendedMetadataStore cache will
            // also short-circuit because we just fetched LIST_METADATA_V2 for
            // these same URIs inside GetRecommendedPlaylistsAsync.
            if (_playlistMetadataPrefetcher is not null)
            {
                foreach (var item in items)
                    _playlistMetadataPrefetcher.EnqueuePlaylistPrefetch(item.Id);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load recommended playlists for {AlbumId}", albumUri);
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
                await _albumService.GetSimilarAlbumsAsync(seedTrack.Uri)).ConfigureAwait(false);

            await RunCurrentAlbumUiAsync(albumId, () =>
            {
                _similarAlbums.ReplaceWith(results);
                HasSimilarAlbums = _similarAlbums.Count > 0;
            }).ConfigureAwait(false);
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
                await _albumService.GetArtistContextAsync(artistUri)).ConfigureAwait(false);

            await RunCurrentAlbumUiAsync(albumId, () =>
            {
                ArtistBioExcerpt = context.BioExcerpt;
                _similarArtists.ReplaceWith(context.SimilarArtists);
                HasSimilarArtists = _similarArtists.Count > 0;
                IsSimilarArtistsLoading = false;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunCurrentAlbumUiAsync(albumId, () => IsSimilarArtistsLoading = false).ConfigureAwait(false);
            _logger?.LogDebug(ex, "Failed to load artist context for {ArtistUri}", artistUri);
        }
    }

    private async Task LoadMusicVideoSignalAsync(string albumId, LazyTrackItem track)
    {
        if (track?.Data is not AlbumTrackDto dto || string.IsNullOrEmpty(dto.Uri)) return;

        try
        {
            var videoUri = await Task.Run(async () =>
                await _albumService.GetMusicVideoUriAsync(dto.Uri)).ConfigureAwait(false);

            await RunCurrentAlbumUiAsync(albumId, () =>
            {
                MusicVideoUri = videoUri;
                HasMusicVideo = !string.IsNullOrEmpty(videoUri);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to fetch music-video signal for {TrackUri}", dto.Uri);
        }
    }

    /// <summary>NPV-sourced "About the artist" data — fired alongside the
    /// existing context fetch. Avatar / verified / monthly-listeners always
    /// come from this path; the bio excerpt overlays the overview-sourced one
    /// when present (which matters for short releases where overview is skipped).
    /// </summary>
    private async Task LoadArtistNpvAsync(string albumId, string artistUri, string leadTrackUri)
    {
        try
        {
            var npv = await Task.Run(async () =>
                await _albumService.GetArtistNpvAsync(artistUri, leadTrackUri)).ConfigureAwait(false);
            if (npv is null)
            {
                await RunCurrentAlbumUiAsync(albumId, () => IsArtistSummaryLoading = false).ConfigureAwait(false);
                return;
            }

            await RunCurrentAlbumUiAsync(albumId, () =>
            {
                ArtistAvatarImageUrl = !string.IsNullOrWhiteSpace(npv.AvatarImageUrl)
                    ? npv.AvatarImageUrl
                    : ArtistImageUrl;
                IsArtistVerified = npv.IsVerified;
                ArtistMonthlyListeners = npv.MonthlyListeners;
                if (!string.IsNullOrEmpty(npv.BioExcerpt))
                    ArtistBioExcerpt = npv.BioExcerpt;
                IsArtistSummaryLoading = false;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunCurrentAlbumUiAsync(albumId, () => IsArtistSummaryLoading = false).ConfigureAwait(false);
            _logger?.LogDebug(ex, "LoadArtistNpvAsync failed for {ArtistUri}", artistUri);
        }
    }

    /// <summary>Reads the canonical artist-followed state from the local likes
    /// store. Mirrors <c>ArtistViewModel.RefreshFollowState</c>'s pattern so
    /// the two pages stay agreement on what "Following" means.</summary>
    private void RefreshArtistFollowState()
    {
        if (_likeService == null || string.IsNullOrEmpty(ArtistId)) return;
        IsArtistFollowing = _likeService.IsSaved(SavedItemType.Artist, ArtistId);
    }

    /// <summary>Optimistic follow/unfollow toggle for the album's lead artist.
    /// Same wire path that the ArtistPage's IsFollowing toggle uses
    /// (<see cref="ITrackLikeService.ToggleSave"/>) — the SaveStateChanged
    /// broadcast keeps every surface in sync.</summary>
    [RelayCommand]
    private void ToggleArtistFollow()
    {
        if (_likeService == null || string.IsNullOrEmpty(ArtistId)) return;
        var wasFollowing = IsArtistFollowing;
        IsArtistFollowing = !wasFollowing;
        _likeService.ToggleSave(SavedItemType.Artist, ArtistId, wasFollowing);
    }

    private async Task LoadSingleTrackContextAsync(string albumId, LazyTrackItem track)
    {
        if (track?.Data is not AlbumTrackDto dto || string.IsNullOrEmpty(dto.Uri))
        {
            await RunCurrentAlbumUiAsync(albumId, () => IsSimilarArtistsLoading = false).ConfigureAwait(false);
            return;
        }

        try
        {
            var ctx = await Task.Run(async () =>
                await _albumService.GetSingleTrackContextAsync(dto.Uri)).ConfigureAwait(false);
            if (ctx == null)
            {
                await RunCurrentAlbumUiAsync(albumId, () => IsSimilarArtistsLoading = false).ConfigureAwait(false);
                return;
            }

            await RunCurrentAlbumUiAsync(albumId, () =>
            {
                MusicVideoUri = ctx.MusicVideoUri;
                HasMusicVideo = !string.IsNullOrEmpty(ctx.MusicVideoUri);

                _similarArtists.ReplaceWith(ctx.RelatedArtists);
                HasSimilarArtists = _similarArtists.Count > 0;
                IsSimilarArtistsLoading = false;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunCurrentAlbumUiAsync(albumId, () => IsSimilarArtistsLoading = false).ConfigureAwait(false);
            _logger?.LogDebug(ex, "Failed to fetch single-track context for {TrackUri}", dto.Uri);
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

    /// <summary>Enqueue every track of this album, in order. Mirrors the
    /// "Add to queue" affordance from the prototype's action cluster. Local
    /// albums aren't supported on the remote queue endpoint, so we no-op.</summary>
    [RelayCommand]
    private void AddAlbumToQueue()
    {
        if (_allTracks.Count == 0) return;
        var trackUris = _allTracks
            .Select(t => t.Data is AlbumTrackDto dto ? dto.Uri : null)
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()
            .ToList();
        if (trackUris.Count == 0) return;
        _playbackStateService.AddToQueue(trackUris);
    }

    /// <summary>Play-next every track of this album, in order. Counterpart to
    /// <see cref="AddAlbumToQueue"/> exposed via the SplitButton's dropdown.
    /// Inserts at the head of the user queue so track 1 plays right after the
    /// current track, track 2 after that, and so on.</summary>
    [RelayCommand]
    private void PlayAlbumNext()
    {
        if (_allTracks.Count == 0) return;
        var trackUris = _allTracks
            .Select(t => t.Data is AlbumTrackDto dto ? dto.Uri : null)
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()
            .ToList();
        if (trackUris.Count == 0) return;
        _playbackStateService.PlayNext(trackUris);
    }

    /// <summary>Seeds Spotify radio from this album's URI. Mirrors the
    /// PlayArtistRadioAsync command on ArtistViewModel; same wire path
    /// (<c>StartRadioAsync</c>) — Spotify accepts any seedable URI here.</summary>
    [RelayCommand]
    private async Task StartAlbumRadioAsync()
    {
        if (string.IsNullOrEmpty(AlbumId)) return;
        var uri = NormalizeAlbumUri(AlbumId);
        var name = AlbumName is { Length: > 0 } n ? $"{n} Radio" : "Album Radio";
        await _playbackStateService.StartRadioAsync(uri, name);
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

    #endregion

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Deactivate();
        DetachLongLivedServices();

        try { _albumAiSummaryCts?.Cancel(); } catch { /* already disposed */ }
        _albumAiSummaryCts = null;

        _allTracks.Clear();
        _popularTrackIds.Clear();
        _filteredTracks.ClearWithoutNotify();
        _alternateReleases.ClearWithoutNotify();
        _moreByArtist.ClearWithoutNotify();
        _merchItems.ClearWithoutNotify();
        _similarAlbums.ClearWithoutNotify();
        _similarArtists.ClearWithoutNotify();
        _recommendedPlaylists.ClearWithoutNotify();
    }
}

public sealed record AiGroundingSourceLink(string Label, Uri NavigateUri);
