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
using Microsoft.UI.Xaml.Media;
using Wavee.Core.Data;
using Wavee.Core.Http;
using Windows.UI;
using Wavee.Core.Playlists;
using Wavee.Core.Session;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Stores;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
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
    private readonly ISession? _session;
    private readonly IPlaylistCacheService? _playlistCache;
    private readonly Services.PlaylistMosaicService? _mosaicService;
    private readonly Services.IUserProfileResolver? _userProfileResolver;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    private List<PlaylistTrackDto> _allTracks = [];
    private readonly DispatcherTimer _searchDebounceTimer;
    private CompositeDisposable? _subscriptions;
    private CancellationTokenSource? _tracksCts;
    private CancellationTokenSource? _followerCountCts;
    private CancellationTokenSource? _paletteCts;
    private string? _tracksLoadedFor;
    private bool _disposed;

    [ObservableProperty]
    private string _playlistId = "";

    [ObservableProperty]
    private string _playlistName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDescriptionViewerCardVisible))]
    private string? _playlistDescription;

    // Notify the same computed when CanEditDescription flips.
    partial void OnCanEditDescriptionChanged(bool value)
        => OnPropertyChanged(nameof(IsDescriptionViewerCardVisible));

    [ObservableProperty]
    private string? _playlistImageUrl;

    // Preserved playlist-level format attributes (editorial chrome +
    // recommender context). Populated when PlaylistDetailDto is received
    // and forwarded into the play command so PlayerState.context_metadata
    // reproduces what Spotify Connect clients expect to see.
    private IReadOnlyDictionary<string, string>? _playlistFormatAttributes;

    // Current 24-byte playlist revision. Needed when POSTing to the
    // session-control signals endpoint.
    private byte[]? _currentRevision;

    // Base62 group id for the session-control-display chip row. Joined
    // into the signal key as "session_control_display$<id>$<option>".
    private string? _sessionControlGroupId;

    // Set to true while BuildSessionControlChips is replacing the chips
    // collection so the OnSelectedSessionControlChipChanged handler doesn't
    // fire a signal for the server-seeded default selection.
    private bool _suppressSessionSignal;

    // Cancellation scope for the in-flight session-control signal POST.
    // Clicking a second chip while the first is still running cancels the
    // prior request and supersedes it.
    private CancellationTokenSource? _sessionSignalCts;

    // Chip whose IsLoading flag is currently lit. Set when a click kicks off
    // the POST; cleared from LoadTracksAsync once the new track list has been
    // applied (or determined unchanged) — that's the visible boundary for
    // "the click is done", and it's what the user expects the chase-border
    // beam to track.
    private SessionControlChipViewModel? _pendingSignalChip;

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

    /// <summary>True when the playlist is open for contribution by other users.
    /// Drives the "Make collaborative" toggle in the owner overflow menu.</summary>
    [ObservableProperty]
    private bool _isCollaborative;

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

    // Granular metadata gates surfaced from PlaylistCapabilitiesDto. Today they
    // all derive from CanEditMetadata in the DTO; when the cache layer starts
    // surfacing per-attribute flags, only the DTO derivation changes — these
    // observable properties stay 1:1 with the new values.
    [ObservableProperty] private bool _canEditMetadata;
    [ObservableProperty] private bool _canEditName;
    [ObservableProperty] private bool _canEditDescription;
    [ObservableProperty] private bool _canEditPicture;
    [ObservableProperty] private bool _canEditCollaborative;
    [ObservableProperty] private bool _canDelete;

    /// <summary>Resolved collaborator list. Populated by
    /// <see cref="RebuildCollaboratorsFromContext"/> from the playlist's owner +
    /// the unique <c>AddedBy</c> users discovered in the track list. The
    /// stubbed members backend isn't a source of truth here — we derive purely
    /// from data that's already on screen.</summary>
    public ObservableCollection<PlaylistMemberResult> Collaborators { get; } = new();

    [ObservableProperty]
    private bool _hasCollaborators;

    /// <summary>Bare owner user id (no <c>spotify:user:</c> prefix). Captured
    /// from the playlist detail; used to identify which addedBy user is the
    /// owner so we don't double-list them in the collaborator stack.</summary>
    [ObservableProperty]
    private string? _ownerId;

    /// <summary>Owner's profile image URL, resolved alongside the display name
    /// in <see cref="ResolveOwnerDisplayNameAsync"/>. Drives the first avatar
    /// in the collaborator stack.</summary>
    [ObservableProperty]
    private string? _ownerAvatarUrl;

    /// <summary>Most recently generated invite link for "Invite collaborators…".
    /// Cleared on playlist swap.</summary>
    [ObservableProperty]
    private PlaylistInviteLink? _latestInviteLink;

    /// <summary>Bare current-user id, used to suppress the "added by" badge on
    /// rows the current user added themselves.</summary>
    public string? CurrentUserId => _session?.GetUserData()?.Username;

    /// <summary>True when the page should render the "…" overflow button — owners
    /// see Delete/Make-collaborative/Invite/Manage; collaborators see Leave.</summary>
    public bool HasOverflowItems =>
        CanDelete || CanEditCollaborative || CanCancelMembership || CanAdministratePermissions;

    /// <summary>
    /// True when the AddedBy column should render. Computed directly from the
    /// current track snapshot so it can't lag a stale <see cref="Collaborators"/>
    /// rebuild: we count distinct non-empty <c>AddedBy</c> values across
    /// <c>_allTracks</c> and require ≥2.
    ///
    /// Cases the rule covers:
    /// <list type="bullet">
    ///   <item>Spotify editorial mixes with empty / uniform <c>addedBy</c> → 0 or 1 distinct → hidden.</item>
    ///   <item>Owned solo personal playlist (every row added by self) → 1 distinct → hidden.</item>
    ///   <item>Viewer of someone else's solo playlist → 1 distinct → hidden.</item>
    ///   <item>Owned playlist that picked up a contributor via an invite-link grant
    ///         (proto's <c>attributes.collaborative</c> flag is NOT toggled by those —
    ///         only the membership list changes) → ≥2 distinct → shown.</item>
    ///   <item>Viewer of a playlist with multiple contributors (David Laid case) → ≥2 distinct → shown.</item>
    /// </list>
    /// </summary>
    public bool ShouldShowAddedByColumn
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _allTracks)
            {
                if (string.IsNullOrEmpty(t.AddedBy)) continue;
                if (seen.Add(t.AddedBy) && seen.Count >= 2)
                    return true;
            }
            return false;
        }
    }

    partial void OnIsCollaborativeChanged(bool value)
    {
        // Toggling "open for collab" should reveal/hide the avatar stack even
        // when the contributor list itself didn't change. The AddedBy column
        // gate no longer depends on this flag — it reads directly from track
        // addedBy values — so no PropertyChanged needed for it here.
        RebuildCollaboratorsFromContext();
    }

    partial void OnCanDeleteChanged(bool value) => OnPropertyChanged(nameof(HasOverflowItems));
    partial void OnCanEditCollaborativeChanged(bool value) => OnPropertyChanged(nameof(HasOverflowItems));
    partial void OnCanCancelMembershipChanged(bool value) => OnPropertyChanged(nameof(HasOverflowItems));
    partial void OnCanAdministratePermissionsChanged(bool value) => OnPropertyChanged(nameof(HasOverflowItems));

    [ObservableProperty]
    private int _followerCount;

    /// <summary>
    /// True while the popcount fetch for the current playlist is in flight.
    /// Drives a shimmer placeholder under the title; goes false on success
    /// (whether the count came back as 0 or a real number) or on cancellation.
    /// </summary>
    [ObservableProperty]
    private bool _isFollowerCountLoading;

    // ── Theme-aware palette (from the playlist cover) ─────────────────────
    // Mirrors AlbumViewModel's palette pipeline: fetched via Pathfinder's
    // fetchPlaylist persisted query, applied per-theme on load and on
    // ActualThemeChanged. Same brush set as the album page so the two
    // surfaces feel like siblings.

    private AlbumPalette? _albumPalette;
    private bool _isDarkTheme;

    /// <summary>Subtle page-wash brush tinted toward the playlist's color. Null when no palette.</summary>
    [ObservableProperty]
    private Brush? _paletteBackdropBrush;

    /// <summary>Hero gradient brush — palette-tinted left-to-right band, theme-aware alpha.</summary>
    [ObservableProperty]
    private Brush? _paletteHeroGradientBrush;

    /// <summary>Accent pill background brush. Null falls back to system accent.</summary>
    [ObservableProperty]
    private Brush? _paletteAccentPillBrush;

    /// <summary>Accent pill foreground brush — auto-computed from accent luminance.</summary>
    [ObservableProperty]
    private Brush? _paletteAccentPillForegroundBrush;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private bool _showOnlyVideoTracks;

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

    /// <summary>
    /// Single dot-separated stats line shown under the owner row, mirroring the
    /// album page's <c>MetaInlineLine</c>. Joins track count, duration, and
    /// follower count with " · ", omitting empty segments — so the line grows
    /// gracefully as values resolve (popcount lands later than tracks).
    /// </summary>
    public string MetaInlineLine
    {
        get
        {
            var parts = new List<string>(3);
            if (TotalTracks > 0)
                parts.Add(TotalTracks == 1 ? "1 song" : $"{TotalTracks} songs");
            if (!string.IsNullOrWhiteSpace(TotalDuration))
                parts.Add(TotalDuration);
            if (!string.IsNullOrWhiteSpace(FollowerCountFormatted))
                parts.Add(FollowerCountFormatted);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>True if the current user follows this playlist (heart filled).
    /// Toggled by <see cref="ToggleFollowAsync"/>; backend wire-up is pending.</summary>
    [ObservableProperty]
    private bool _isFollowed;

    /// <summary>
    /// Visibility gate for the resolved follower-count text. False while the
    /// popcount fetch is in flight (the shimmer takes the slot) and false when
    /// the playlist has no followers / hides its count (slot collapses entirely).
    /// </summary>
    public bool ShowFollowerCountText
        => !IsFollowerCountLoading && !string.IsNullOrEmpty(FollowerCountFormatted);

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

    public int VideoTrackCount => _allTracks.Count(static track => track.HasVideo);
    public bool HasVideoTracks => VideoTrackCount > 0;
    public string VideoTrackFilterLabel => VideoTrackCount == 1 ? "1 video" : $"{VideoTrackCount} videos";

    public bool CanRemove => CanEditItems && HasSelection;

    /// <summary>
    /// True when the read-only description card should render — i.e. the user
    /// CANNOT edit the description (so we keep the existing RichTextBlock +
    /// hyperlink path) AND the playlist actually carries a description. Editors
    /// get the editable branch instead, which is always visible (it shows the
    /// placeholder when empty so they can add one).
    /// </summary>
    public bool IsDescriptionViewerCardVisible => !CanEditDescription && !string.IsNullOrEmpty(PlaylistDescription);

    public PlaylistViewModel(
        ILibraryDataService libraryDataService,
        IPlaybackStateService playbackStateService,
        PlaylistStore playlistStore,
        ILogger<PlaylistViewModel>? logger = null,
        Services.PlaylistMosaicService? mosaicService = null,
        Services.IUserProfileResolver? userProfileResolver = null,
        ISession? session = null,
        IPlaylistCacheService? playlistCache = null)
    {
        _libraryDataService = libraryDataService;
        _playbackStateService = playbackStateService;
        _playlistStore = playlistStore;
        _session = session;
        _playlistCache = playlistCache;
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

        Diagnostics.LiveInstanceTracker.Register(this);
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
        OnPropertyChanged(nameof(ShowFollowerCountText));
        OnPropertyChanged(nameof(MetaInlineLine));
    }

    partial void OnTotalTracksChanged(int value) => OnPropertyChanged(nameof(MetaInlineLine));
    partial void OnTotalDurationChanged(string value) => OnPropertyChanged(nameof(MetaInlineLine));

    partial void OnIsFollowerCountLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFollowerCountText));
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

    // OnIsOwnerChanged consolidated above (line ~192) — also raises
    // ShouldShowAddedByColumn so the AddedBy column gates correctly.

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
            filtered = filtered.Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.ArtistName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.AlbumName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (ShowOnlyVideoTracks)
            filtered = filtered.Where(static t => t.HasVideo);

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
            OwnerId = null;
            OwnerAvatarUrl = null;
            Collaborators.Clear();
            HasCollaborators = false;
            // Reset the track snapshot so ApplyDetail's RebuildCollaboratorsFromContext
            // doesn't compute the AddedBy gate against the previous playlist's tracks
            // (which produced wrong stale-true gate values during the brief window
            // between Activate and LoadTracksAsync — those would latch into
            // already-materializing ListView containers).
            _allTracks = new List<PlaylistTrackDto>();
            OnPropertyChanged(nameof(ShouldShowAddedByColumn));
            _tracksLoadedFor = null;
            ShowOnlyVideoTracks = false;
            NotifyVideoFilterProperties();
            _albumPalette = null;
            PaletteBackdropBrush = null;
            PaletteHeroGradientBrush = null;
            PaletteAccentPillBrush = null;
            PaletteAccentPillForegroundBrush = null;
            IsOwner = false;
            IsPublic = false;
            IsCollaborative = false;
            FollowerCount = 0;
            TotalTracks = 0;
            _playlistFormatAttributes = null;
            _currentRevision = null;
            _sessionControlGroupId = null;
            TotalDuration = string.Empty;
            HasAnyAddedAt = false;

            // Reset granular capability gates so a stale "owner-only" affordance
            // doesn't briefly leak into a viewer-mode playlist render.
            CanEditMetadata = false;
            CanEditName = false;
            CanEditDescription = false;
            CanEditPicture = false;
            CanEditCollaborative = false;
            CanDelete = false;

            // Drop the previous playlist's collaborator state.
            Collaborators.Clear();
            HasCollaborators = false;
            LatestInviteLink = null;

            // Drop chips from the previous playlist before the new DTO arrives
            // so they don't flicker visible during the reload.
            _suppressSessionSignal = true;
            SessionControlChips.Clear();
            SelectedSessionControlChip = null;
            _suppressSessionSignal = false;
            OnPropertyChanged(nameof(HasSessionControlChips));
            _sessionSignalCts?.Cancel();
            _sessionSignalCts?.Dispose();
            _sessionSignalCts = null;

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
        _followerCountCts?.Cancel();
        _followerCountCts?.Dispose();
        _followerCountCts = null;
        _paletteCts?.Cancel();
        _paletteCts?.Dispose();
        _paletteCts = null;
        // Search timer holds a Tick closure over `this`; stop it on nav-away so it
        // doesn't fire against a cached-but-hidden page.
        _searchDebounceTimer.Stop();
    }

    /// <summary>
    /// Heavy-state release for cached pages going off-screen. Drops the track
    /// grid and collaborator state — these are the bound collections that pin
    /// the most realized item containers (and therefore composition memory)
    /// while the page sits invisible in the Frame cache.
    ///
    /// Lightweight identity (PlaylistId, name, image URL, palette brushes) is
    /// preserved so the hero still renders correctly between re-Activate and
    /// the BehaviorSubject re-emitting. Setting <c>_tracksLoadedFor = null</c>
    /// forces Activate's <c>isNewPlaylist</c> branch on revisit so the shimmer
    /// re-seeds before the warm store value lands.
    /// </summary>
    public void Hibernate()
    {
        _logger?.LogInformation("Hibernate: playlistId='{PlaylistId}'", PlaylistId);
        Deactivate();
        _tracksLoadedFor = null;

        FilteredTracks.Clear();
        _allTracks = new List<PlaylistTrackDto>();
        OnPropertyChanged(nameof(ShouldShowAddedByColumn));
        ShowOnlyVideoTracks = false;
        NotifyVideoFilterProperties();
        Collaborators.Clear();
        HasCollaborators = false;
        _suppressSessionSignal = true;
        SessionControlChips.Clear();
        SelectedSessionControlChip = null;
        _suppressSessionSignal = false;
        OnPropertyChanged(nameof(HasSessionControlChips));
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
        _currentRevision = detail.Revision;
        _sessionControlGroupId = detail.SessionControlGroupId;
        BuildSessionControlChips(detail.SessionControlOptions);
        if (!string.IsNullOrEmpty(detail.OwnerName) && detail.OwnerName != "Unknown")
            OwnerName = detail.OwnerName;
        // Stash the owner id (bare or full URI tolerated, both flow through the
        // resolver) so RebuildCollaboratorsFromContext can dedupe against the
        // unique addedBy set below.
        OwnerId = string.IsNullOrWhiteSpace(detail.OwnerId)
            ? detail.OwnerName
            : detail.OwnerId;

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
        IsCollaborative = detail.IsCollaborative;
        FollowerCount = detail.FollowerCount;

        // Popcount runs out-of-band — the data layer holds FollowerCount at 0
        // so the detail load doesn't block on a stat-only round trip. Kick off
        // the dedicated fetch here and let the chip shimmer until it resolves.
        // Same idea for the palette: Pathfinder fetchPlaylist is fired in
        // parallel and the hero tints in once the colour set arrives.
        if (!string.IsNullOrEmpty(PlaylistId))
        {
            _ = LoadFollowerCountAsync(PlaylistId);
            _ = LoadPaletteAsync(PlaylistId);
        }

        BasePermission = detail.BasePermission;
        CanEditItems = detail.Capabilities.CanEditItems;
        CanAdministratePermissions = detail.Capabilities.CanAdministratePermissions;
        CanCancelMembership = detail.Capabilities.CanCancelMembership;
        CanAbuseReport = detail.Capabilities.CanAbuseReport;

        // Granular metadata gates — drive RenameAsync / UpdateDescriptionAsync /
        // ChangeCoverAsync / DeletePlaylistAsync / ToggleCollaborativeAsync via
        // their respective CanExecute predicates instead of the coarse IsOwner.
        CanEditMetadata = detail.Capabilities.CanEditMetadata;
        CanEditName = detail.Capabilities.CanEditName;
        CanEditDescription = detail.Capabilities.CanEditDescription;
        CanEditPicture = detail.Capabilities.CanEditPicture;
        CanEditCollaborative = detail.Capabilities.CanEditCollaborative;
        CanDelete = detail.Capabilities.CanDelete;

        _logger?.LogInformation(
            "[caps] VM ApplyDetail '{Id}': IsOwner={IsOwner} BasePerm={Base} | dto.Caps=[EditItems={EI},EditMeta={EM},Delete={DD},Admin={AD}] | VM gates=[CanEditName={CEN},CanEditDescription={CED},CanEditPicture={CEP},CanEditCollab={CEC},CanDelete={CD}]",
            PlaylistId, IsOwner, BasePermission,
            detail.Capabilities.CanEditItems, detail.Capabilities.CanEditMetadata,
            detail.Capabilities.CanDelete, detail.Capabilities.CanAdministratePermissions,
            CanEditName, CanEditDescription, CanEditPicture, CanEditCollaborative, CanDelete);

        // Re-evaluate command CanExecute now that gates have refreshed.
        RenameCommand.NotifyCanExecuteChanged();
        UpdateDescriptionCommand.NotifyCanExecuteChanged();
        ChangeCoverCommand.NotifyCanExecuteChanged();
        RemoveCoverCommand.NotifyCanExecuteChanged();
        DeletePlaylistCommand.NotifyCanExecuteChanged();
        ToggleCollaborativeCommand.NotifyCanExecuteChanged();

        // The mutating members backend (LoadCollaboratorsAsync) is still a stub,
        // so it isn't called here — the collaborator stack is fed by data we
        // already have on screen instead. Seed it now with whatever we know
        // (owner only at this point); LoadTracksAsync + addedBy resolution will
        // call rebuild again as track contributors materialise.
        RebuildCollaboratorsFromContext();

        HasError = false;
        ErrorMessage = null;
    }

    // Rebuild the session-control chip row from the DTO's pre-parsed options.
    // Preserves the previously-selected option across refreshes (e.g. a Mercury
    // push that bumps the revision but doesn't change the chip set) so the
    // user's current selection survives. Suppresses the SelectedChip change
    // handler while re-seeding so we don't re-send a signal for a noop.
    private void BuildSessionControlChips(IReadOnlyList<SessionControlOption>? options)
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
            if (_playlistFormatAttributes is not null &&
                _playlistFormatAttributes.TryGetValue("session_control.selected_signals", out var raw) &&
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
        if (_session is null || _playlistCache is null)
        {
            _logger?.LogDebug("Session control chip selected but Session/PlaylistCache not wired; ignoring");
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
        if (_currentRevision is null || _currentRevision.Length == 0)
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

        _ = ApplySessionControlSignalAsync(oldValue, newValue, ct);
    }

    private async Task ApplySessionControlSignalAsync(
        SessionControlChipViewModel? oldValue,
        SessionControlChipViewModel newValue,
        CancellationToken ct)
    {
        var playlistId = PlaylistId;
        var revision = _currentRevision;
        var requestId = Guid.NewGuid().ToString();
        // Use the server-advertised identifier verbatim — no client-side
        // derivation. Each chip has its own unique group id embedded; the
        // pair is held by newValue.SignalIdentifier.
        var signalKey = newValue.SignalIdentifier!;

        try
        {
            // Capture the POST response — Spotify ships the re-personalised
            // SelectedListContent inline. No need for a follow-up GET (which
            // would race the server's signal-processing pipeline) or for
            // /diff (which 509s on editorial mixes). We hand the bytes
            // straight to the cache, which maps + persists + emits Changes.
            var freshContent = await _session!.SpClient.SendPlaylistSignalAsync(
                playlistId,
                revision!,
                signalKey,
                requestId,
                ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested || PlaylistId != playlistId)
                return;

            await _playlistCache!.ApplyFreshContentAsync(playlistId, freshContent, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || PlaylistId != playlistId)
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
                    && !string.Equals(OwnerName, displayName, StringComparison.Ordinal))
                {
                    OwnerName = displayName;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(avatarUrl)
                    && !string.Equals(OwnerAvatarUrl, avatarUrl, StringComparison.Ordinal))
                {
                    OwnerAvatarUrl = avatarUrl;
                    changed = true;
                }
                if (changed)
                    RebuildCollaboratorsFromContext();
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

    /// <summary>
    /// Background follower-count fetch. Held out of the main detail-load path so
    /// the playlist UI renders immediately and the count chip shimmers in
    /// asynchronously when the popcount endpoint replies.
    /// </summary>
    private async Task LoadFollowerCountAsync(string playlistId)
    {
        _followerCountCts?.Cancel();
        _followerCountCts?.Dispose();
        _followerCountCts = new CancellationTokenSource();
        var ct = _followerCountCts.Token;

        IsFollowerCountLoading = true;
        try
        {
            var count = await _libraryDataService
                .GetPlaylistFollowerCountAsync(playlistId, ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            _dispatcherQueue.TryEnqueue(() =>
            {
                // A nav-swap between fetch start and result arrival would otherwise
                // paint the previous playlist's count under the new one's title.
                if (_disposed || !string.Equals(PlaylistId, playlistId, StringComparison.Ordinal))
                    return;
                FollowerCount = (int)Math.Min(count, int.MaxValue);
                IsFollowerCountLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer fetch — leave the loading flag for the new run to manage.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LoadFollowerCountAsync failed for {PlaylistId}", playlistId);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || !string.Equals(PlaylistId, playlistId, StringComparison.Ordinal))
                    return;
                IsFollowerCountLoading = false;
            });
        }
    }

    /// <summary>
    /// Background palette fetch via Pathfinder's fetchPlaylist persisted query.
    /// Runs in parallel with the main detail load so the hero starts in a
    /// neutral state and tints in once the colour set lands. Mirrors
    /// AlbumViewModel's palette pipeline so the two surfaces look like siblings.
    /// </summary>
    private async Task LoadPaletteAsync(string playlistId)
    {
        _paletteCts?.Cancel();
        _paletteCts?.Dispose();
        _paletteCts = new CancellationTokenSource();
        var ct = _paletteCts.Token;

        try
        {
            var palette = await _libraryDataService
                .GetPlaylistPaletteAsync(playlistId, ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || !string.Equals(PlaylistId, playlistId, StringComparison.Ordinal))
                    return;
                _albumPalette = palette;
                ApplyTheme(_isDarkTheme);
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer fetch — silent.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LoadPaletteAsync failed for {PlaylistId}", playlistId);
        }
    }

    /// <summary>
    /// Theme-aware palette refresh. Called by the page on init + on
    /// ActualThemeChanged. Mirrors <c>AlbumViewModel.ApplyTheme</c>: dark theme
    /// uses HigherContrast (deepest), light theme uses HighContrast (saturated
    /// but a step brighter). MinContrast is skipped — too pastel for white
    /// overlay text. When no palette is available the brushes are nulled so
    /// the page renders untinted.
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
        var accent = Color.FromArgb(255, tier.TextAccentR, tier.TextAccentG, tier.TextAccentB);

        PaletteBackdropBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(isDark ? 60 : 38), bg.R, bg.G, bg.B));

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

        PaletteAccentPillBrush = new SolidColorBrush(accent);
        var accentLuma = (accent.R * 299 + accent.G * 587 + accent.B * 114) / 1000;
        PaletteAccentPillForegroundBrush = new SolidColorBrush(
            accentLuma > 160 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255));
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
                    ApplyVideoAvailabilityToCurrentTracks(tracks);
                    _tracksLoadedFor = playlistId;
                    IsLoadingTracks = false;
                    ClearPendingSignalChip();
                    _logger?.LogInformation(
                        "Tracks unchanged after refresh: {Count} same Ids for '{PlaylistId}' first3={First3}",
                        tracks.Count, playlistId,
                        string.Join(",", tracks.Take(3).Select(t => t.Id)));
                    return;
                }

                _allTracks = tracks.Select((t, i) => t with { OriginalIndex = i + 1 }).ToList();
                HasAnyAddedAt = _allTracks.Any(t => t.AddedAt.HasValue);
                NotifyVideoFilterProperties();
                UpdateAggregates();
                ApplyFilterAndSort();
                _tracksLoadedFor = playlistId;
                IsLoadingTracks = false;
                ClearPendingSignalChip();
                _logger?.LogInformation(
                    "Tracks applied: {Count} tracks for '{PlaylistId}' first3={First3}",
                    _allTracks.Count, playlistId,
                    string.Join(",", _allTracks.Take(3).Select(t => t.Id)));

                // _allTracks is populated — the unique addedBy set is now derivable.
                // Rebuild seeds the stack with bare-id contributors immediately;
                // ResolveAddedByUsernamesAsync upgrades the names + avatars below
                // and calls rebuild again when its writeback completes.
                RebuildCollaboratorsFromContext();
                // Defensive: ShouldShowAddedByColumn reads directly from
                // _allTracks (which we just reassigned), and the rebuild above
                // already raised this — but raising again immediately after
                // the assignment makes the dependency obvious if rebuild's
                // behaviour changes later.
                OnPropertyChanged(nameof(ShouldShowAddedByColumn));

                // Background addedBy resolution — fills AddedByDisplayName /
                // AddedByAvatarUrl on each DTO. Runs whenever the AddedBy column
                // will be visible (collab playlists OR any playlist where the
                // current user isn't the owner) so the cells don't fall back to
                // the long bare-id "@…" rendering. Captures the playlistId so a
                // stale resolution doesn't write into a swapped page.
                if (ShouldShowAddedByColumn)
                    _ = ResolveAddedByUsernamesAsync(playlistId, ct);
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

    // Stop the per-chip chase-border beam. Called from LoadTracksAsync once
    // the post-click track list has been applied (or determined unchanged) —
    // the visible boundary the user expects the beam to track.
    private void ClearPendingSignalChip()
    {
        if (_pendingSignalChip is { } pending)
        {
            pending.IsLoading = false;
            _pendingSignalChip = null;
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

    private void ApplyVideoAvailabilityToCurrentTracks(IReadOnlyList<PlaylistTrackDto> fetched)
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
    private void OpenOwnerProfile()
    {
        if (string.IsNullOrWhiteSpace(OwnerId)) return;
        var bareId = ExtractBareUserId(OwnerId);
        if (string.IsNullOrWhiteSpace(bareId)) return;

        var param = new Wavee.UI.WinUI.Data.Parameters.ContentNavigationParameter
        {
            Uri = $"spotify:user:{bareId}",
            Title = OwnerName,
            ImageUrl = OwnerAvatarUrl
        };
        Helpers.Navigation.NavigationHelpers.OpenProfile(
            param,
            OwnerName,
            Helpers.Navigation.NavigationHelpers.IsCtrlPressed());
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

    /// <summary>
    /// Toggles whether the current user follows this playlist. Visual flip is
    /// optimistic — backend wire-up via
    /// <see cref="ILibraryDataService.SetPlaylistFollowedAsync"/> is currently
    /// stubbed; the call shape exists so the page chrome behaves correctly.
    /// </summary>
    [RelayCommand]
    private async Task ToggleFollowAsync()
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;
        var nextValue = !IsFollowed;
        IsFollowed = nextValue;
        try
        {
            await _libraryDataService
                .SetPlaylistFollowedAsync(PlaylistId, nextValue)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Revert the optimistic flip on failure so the heart visual matches
            // the actual backend state. Logged at Debug — the backend is stubbed
            // for now so this is mostly a safety net for the future wire-up.
            _logger?.LogDebug(ex, "ToggleFollowAsync failed for {PlaylistId} — reverting", PlaylistId);
            IsFollowed = !nextValue;
        }
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

    /// <summary>
    /// Resolves display name + avatar for every distinct <c>AddedBy</c> on the
    /// current playlist (excluding the current user, whose row collapses the
    /// AddedBy cell), and writes the results back into the <c>PlaylistTrackDto</c>
    /// instances. Each successful write fires PropertyChanged on the affected
    /// DTO so already-realized cells re-render without a full grid rebuild.
    /// </summary>
    private async Task ResolveAddedByUsernamesAsync(string forPlaylistId, CancellationToken ct)
    {
        if (_userProfileResolver is null)
        {
            _logger?.LogInformation("[addedby] resolver=null, skipping for '{Id}'", forPlaylistId);
            return;
        }

        // Snapshot the current track list so we don't race with a swap.
        // Resolve every distinct addedBy id INCLUDING the current user — on a
        // collaborative playlist Spotify shows your own name in the AddedBy
        // column too (so a glance at the column tells you "I added these, X
        // added those"). The previous skip-self filter was the right default
        // back when the column was hidden on owner-mode personal playlists,
        // but the column gate now covers multi-contributor playlists where
        // the self rows would otherwise render as blank.
        var snapshot = _allTracks;
        var unique = snapshot
            .Select(t => t.AddedBy)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger?.LogInformation(
            "[addedby] resolve start for '{Id}' — uniqueCount={N} unique=[{List}]",
            forPlaylistId, unique.Count, string.Join(",", unique));

        if (unique.Count == 0) return;

        var lookup = new Dictionary<string, UserProfileSummary?>(StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(unique.Select(async id =>
        {
            try
            {
                var profile = await _userProfileResolver.GetProfileAsync(id, ct).ConfigureAwait(false);
                lock (lookup) lookup[id] = profile;
                _logger?.LogInformation(
                    "[addedby] resolved '{Id}' -> name={Name} avatar={Avatar}",
                    id,
                    profile?.DisplayName ?? "<null>",
                    string.IsNullOrEmpty(profile?.AvatarUrl) ? "<null>" : "set");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[addedby] resolve failed for '{Id}'", id);
            }
        })).ConfigureAwait(true);

        // Bail if a swap landed mid-resolve.
        if (PlaylistId != forPlaylistId || ct.IsCancellationRequested)
        {
            _logger?.LogInformation(
                "[addedby] swap detected mid-resolve for '{For}' (current PlaylistId='{Cur}'), aborting",
                forPlaylistId, PlaylistId);
            return;
        }

        var anyChanged = 0;
        var skippedNullProfile = 0;
        foreach (var dto in snapshot)
        {
            if (string.IsNullOrEmpty(dto.AddedBy)) continue;
            if (!lookup.TryGetValue(dto.AddedBy, out var profile) || profile is null)
            {
                skippedNullProfile++;
                continue;
            }
            dto.AddedByDisplayName = profile.DisplayName ?? dto.AddedBy;
            dto.AddedByAvatarUrl = profile.AvatarUrl;
            anyChanged++;
        }

        _logger?.LogInformation(
            "[addedby] writeback complete for '{Id}': mutated={Changed} skippedNullProfile={Nul}",
            forPlaylistId, anyChanged, skippedNullProfile);

        // The TrackDataGrid pushes formatter values imperatively at row
        // materialization, so DTO mutations don't reach already-rendered
        // cells. Signal the page to walk visible rows and re-invoke the
        // AddedByFormatter so the resolved name + avatar replace the
        // bare-id "@…" fallback.
        if (anyChanged > 0)
        {
            _logger?.LogInformation("[addedby] firing AddedByResolved event for '{Id}'", forPlaylistId);
            AddedByResolved?.Invoke(this, EventArgs.Empty);

            // Resolved names + avatars are now on the per-track DTOs — rebuild
            // the collaborator stack so the placeholder bare-id entries upgrade
            // to friendly avatars in the same beat as the AddedBy column.
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || PlaylistId != forPlaylistId)
                    return;
                RebuildCollaboratorsFromContext();
            });
        }
    }

    /// <summary>Fired after <see cref="ResolveAddedByUsernamesAsync"/> writes
    /// resolved display names + avatars back into the playlist's track DTOs.
    /// PlaylistPage uses this to call <c>TrackGrid.RefreshAddedByCells()</c>.</summary>
    public event EventHandler? AddedByResolved;

    // ── Members + invite + leave (collab playlist UI) ────────────────────────

    /// <summary>
    /// Builds the collaborator list shown in the hero avatar stack from data
    /// already on screen — the playlist owner plus the unique <c>AddedBy</c>
    /// users discovered across <c>_allTracks</c>. Independent of the stubbed
    /// members backend (<see cref="LoadCollaboratorsAsync"/>) so the stack
    /// works on any playlist with multiple contributors, not just ones the
    /// current user can administrate.
    ///
    /// Visibility rule: stack is shown when the playlist is open for collab
    /// (<see cref="IsCollaborative"/>) OR when ≥2 unique contributors are
    /// present. Single-owner non-collab playlists collapse to nothing.
    /// </summary>
    private void RebuildCollaboratorsFromContext()
    {
        // Snapshot the track list — this method is dispatcher-thread but the
        // backing field is reassigned on the same thread by LoadTracksAsync, so
        // a local read keeps the dedupe stable.
        var tracks = _allTracks;
        var ownerId = string.IsNullOrEmpty(OwnerId)
            ? string.Empty
            : ExtractBareUserId(OwnerId);

        var members = new List<PlaylistMemberResult>(capacity: 8);

        // Owner always leads the stack — even when no other contributors exist
        // yet, the single avatar serves as the "open for collaboration"
        // affordance on collaborative playlists.
        if (!string.IsNullOrEmpty(ownerId) || !string.IsNullOrEmpty(OwnerName))
        {
            members.Add(new PlaylistMemberResult
            {
                UserId = ownerId,
                Username = ownerId,
                DisplayName = string.IsNullOrWhiteSpace(OwnerName) ? null : OwnerName,
                AvatarUrl = OwnerAvatarUrl,
                Role = PlaylistMemberRole.Owner,
            });
        }

        // Unique addedBy contributors, owner excluded. The display name +
        // avatar come from whichever track DTO carries the resolved values
        // (ResolveAddedByUsernamesAsync writes the same value to every track
        // by the same user, so any one suffices).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(ownerId)) seen.Add(ownerId);

        foreach (var t in tracks)
        {
            var addedBy = t.AddedBy;
            if (string.IsNullOrEmpty(addedBy)) continue;
            if (!seen.Add(addedBy)) continue;

            members.Add(new PlaylistMemberResult
            {
                UserId = addedBy,
                Username = addedBy,
                DisplayName = string.IsNullOrWhiteSpace(t.AddedByDisplayName) ? null : t.AddedByDisplayName,
                AvatarUrl = t.AddedByAvatarUrl,
                Role = PlaylistMemberRole.Contributor,
            });
        }

        // Replace the collection in one shot — RebuildCollaboratorStack in the
        // page subscribes to CollectionChanged and rebuilds visuals on any
        // mutation, so we want a single Reset rather than incremental edits.
        Collaborators.Clear();
        foreach (var m in members)
            Collaborators.Add(m);

        HasCollaborators = IsCollaborative || Collaborators.Count >= 2;

        // The AddedBy gate reads directly off _allTracks (no observable source)
        // so re-fire its change so the page-side OneWay binding picks up the
        // new value whenever the track set has been rebuilt.
        OnPropertyChanged(nameof(ShouldShowAddedByColumn));

        _logger?.LogInformation(
            "[collab-stack] rebuilt: count={Count} hasCollab={Has} isCollab={IsCollab} ownerId={Owner}",
            Collaborators.Count, HasCollaborators, IsCollaborative, ownerId);

        var uniqueAddedBys = _allTracks
            .Select(t => t.AddedBy)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        _logger?.LogInformation(
            "[addedby-gate] '{Id}' uniqueAddedBys={N} → ShouldShow={Show}",
            PlaylistId, uniqueAddedBys, ShouldShowAddedByColumn);
    }

    private static string ExtractBareUserId(string idOrUri)
    {
        const string prefix = "spotify:user:";
        return idOrUri.StartsWith(prefix, StringComparison.Ordinal)
            ? idOrUri[prefix.Length..]
            : idOrUri;
    }

    /// <summary>Loads the collaborator list and resolves display names + avatars.
    /// Dormant — pending the real members backend wire-up. The visual avatar
    /// stack derives from track data via <see cref="RebuildCollaboratorsFromContext"/>;
    /// this method is retained for the admin "Manage members" flyout, which still
    /// needs the role-aware list once the backend lands.</summary>
    [RelayCommand]
    private async Task LoadCollaboratorsAsync()
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;

        try
        {
            var raw = await _libraryDataService
                .GetPlaylistMembersAsync(PlaylistId)
                .ConfigureAwait(true);

            // Resolve display name + avatar in parallel; UserProfileResolver
            // memoises so repeated calls cost nothing on cache hits.
            var enriched = await Task.WhenAll(raw.Select(async m =>
            {
                if (_userProfileResolver is null) return m;
                var profile = await _userProfileResolver
                    .GetProfileAsync(m.UserId)
                    .ConfigureAwait(true);
                return m with
                {
                    DisplayName = profile?.DisplayName ?? m.DisplayName,
                    AvatarUrl = profile?.AvatarUrl ?? m.AvatarUrl
                };
            })).ConfigureAwait(true);

            Collaborators.Clear();
            foreach (var m in enriched) Collaborators.Add(m);
            HasCollaborators = Collaborators.Count > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadCollaboratorsAsync failed for '{Id}'", PlaylistId);
        }
    }

    /// <summary>Optimistically updates a member's role; reverts on failure.</summary>
    [RelayCommand(CanExecute = nameof(CanAdministratePermissions))]
    private async Task SetMemberRoleAsync((string memberUserId, PlaylistMemberRole role) args)
    {
        if (string.IsNullOrEmpty(PlaylistId) || string.IsNullOrEmpty(args.memberUserId)) return;

        var existing = Collaborators.FirstOrDefault(m =>
            string.Equals(m.UserId, args.memberUserId, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;

        var previous = existing.Role;
        var index = Collaborators.IndexOf(existing);
        Collaborators[index] = existing with { Role = args.role };

        try
        {
            await _libraryDataService
                .SetPlaylistMemberRoleAsync(PlaylistId, args.memberUserId, args.role)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SetMemberRoleAsync failed for '{Id}'/'{Member}'", PlaylistId, args.memberUserId);
            Collaborators[index] = existing with { Role = previous };
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't update permission", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>Optimistically removes a member; restores on failure.</summary>
    [RelayCommand(CanExecute = nameof(CanAdministratePermissions))]
    private async Task RemoveMemberAsync(string memberUserId)
    {
        if (string.IsNullOrEmpty(PlaylistId) || string.IsNullOrEmpty(memberUserId)) return;

        var existing = Collaborators.FirstOrDefault(m =>
            string.Equals(m.UserId, memberUserId, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;

        var index = Collaborators.IndexOf(existing);
        Collaborators.RemoveAt(index);
        HasCollaborators = Collaborators.Count > 0;

        try
        {
            await _libraryDataService
                .RemovePlaylistMemberAsync(PlaylistId, memberUserId)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RemoveMemberAsync failed for '{Id}'/'{Member}'", PlaylistId, memberUserId);
            Collaborators.Insert(index, existing);
            HasCollaborators = Collaborators.Count > 0;
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't remove member", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>Generates a new invite link with the given TTL and stores it on
    /// <see cref="LatestInviteLink"/>. The view's invite flyout watches that
    /// property to swap from the "Generate" CTA to the URL display.</summary>
    [RelayCommand(CanExecute = nameof(CanEditCollaborative))]
    private async Task CreateInviteLinkAsync(TimeSpan ttl)
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;
        if (ttl <= TimeSpan.Zero) ttl = TimeSpan.FromDays(7);

        try
        {
            LatestInviteLink = await _libraryDataService
                .CreatePlaylistInviteLinkAsync(PlaylistId, PlaylistMemberRole.Contributor, ttl)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "CreateInviteLinkAsync failed for '{Id}'", PlaylistId);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't generate invite link", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>Collaborator-only: leave the playlist. The page is expected to
    /// confirm before invoking, and to navigate away on success.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelMembership))]
    private async Task LeavePlaylistAsync()
    {
        if (string.IsNullOrEmpty(PlaylistId) || string.IsNullOrEmpty(CurrentUserId)) return;

        try
        {
            await _libraryDataService
                .RemovePlaylistMemberAsync(PlaylistId, CurrentUserId)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LeavePlaylistAsync failed for '{Id}'", PlaylistId);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't leave playlist", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
    }

    // ── Inline edit commands (Phase 1: rename + description) ────────────────

    /// <summary>True while a metadata edit (rename or description) is being saved.
    /// The view binds this to the InlineEditableText's IsBusy spinner.</summary>
    [ObservableProperty] private bool _isRenaming;

    [ObservableProperty] private bool _isUpdatingDescription;

    /// <summary>
    /// Optimistically sets <see cref="PlaylistName"/> to <paramref name="newName"/>
    /// and persists via <see cref="ILibraryDataService.RenamePlaylistAsync"/>. On
    /// failure the previous name is restored and a toast is shown.
    /// Trims whitespace; rejects empty names (silent revert).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditName))]
    private async Task RenameAsync(string newName)
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;

        var trimmed = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, PlaylistName, StringComparison.Ordinal))
            return;

        var previous = PlaylistName;
        PlaylistName = trimmed;
        IsRenaming = true;
        try
        {
            await _libraryDataService.RenamePlaylistAsync(PlaylistId, trimmed).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RenameAsync failed for playlist '{Id}'; reverting", PlaylistId);
            PlaylistName = previous;
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't rename playlist", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
        finally
        {
            IsRenaming = false;
        }
    }

    /// <summary>True while a cover-photo upload is in flight.
    /// Drives the spinner overlay on the cover edit affordance.</summary>
    [ObservableProperty] private bool _isUploadingCover;

    /// <summary>
    /// Persists a freshly-picked cover image. <paramref name="jpegBytes"/> must
    /// already be a JPEG ≤256 KB (use <c>PlaylistCoverHelper</c>). On failure
    /// the page reverts the local preview and shows a toast; on success the
    /// stored URL refreshes from the next AlbumStore push.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditPicture))]
    private async Task ChangeCoverAsync(byte[] jpegBytes)
    {
        if (string.IsNullOrEmpty(PlaylistId) || jpegBytes is null || jpegBytes.Length == 0)
            return;

        IsUploadingCover = true;
        try
        {
            await _libraryDataService.UpdatePlaylistCoverAsync(PlaylistId, jpegBytes).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ChangeCoverAsync failed for playlist '{Id}'", PlaylistId);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't update cover photo", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
            throw; // let the page revert its local preview
        }
        finally
        {
            IsUploadingCover = false;
        }
    }

    /// <summary>
    /// Deletes the playlist (Spotify implements this as the owner unfollowing
    /// their own playlist). The page should navigate away on success.
    /// Caller is expected to confirm with the user before invoking.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeletePlaylistAsync()
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;

        try
        {
            await _libraryDataService.DeletePlaylistAsync(PlaylistId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DeletePlaylistAsync failed for playlist '{Id}'", PlaylistId);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't delete playlist", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>
    /// Toggles the playlist between owner-only and collaborative. Optimistically
    /// flips <see cref="IsCollaborative"/>; reverts on failure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditCollaborative))]
    private async Task ToggleCollaborativeAsync()
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;

        var previous = IsCollaborative;
        var next = !previous;
        IsCollaborative = next;
        try
        {
            await _libraryDataService.SetPlaylistCollaborativeAsync(PlaylistId, next).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ToggleCollaborativeAsync failed for playlist '{Id}'; reverting", PlaylistId);
            IsCollaborative = previous;
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't update sharing setting", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>
    /// Removes the custom cover and reverts to the auto-generated mosaic.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditPicture))]
    private async Task RemoveCoverAsync()
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;

        IsUploadingCover = true;
        try
        {
            await _libraryDataService.RemovePlaylistCoverAsync(PlaylistId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RemoveCoverAsync failed for playlist '{Id}'", PlaylistId);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't remove cover photo", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
        finally
        {
            IsUploadingCover = false;
        }
    }

    /// <summary>
    /// Optimistically sets <see cref="PlaylistDescription"/> to
    /// <paramref name="newDescription"/> and persists. Empty string clears the
    /// description on the server. On failure the previous value is restored.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditDescription))]
    private async Task UpdateDescriptionAsync(string newDescription)
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;

        var value = newDescription ?? string.Empty;
        if (string.Equals(value, PlaylistDescription ?? string.Empty, StringComparison.Ordinal))
            return;

        var previous = PlaylistDescription;
        PlaylistDescription = value;
        IsUpdatingDescription = true;
        try
        {
            await _libraryDataService.UpdatePlaylistDescriptionAsync(PlaylistId, value).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "UpdateDescriptionAsync failed for playlist '{Id}'; reverting", PlaylistId);
            PlaylistDescription = previous;
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't update description", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
        finally
        {
            IsUpdatingDescription = false;
        }
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

/// <summary>
/// One chip in the <see cref="PlaylistViewModel.SessionControlChips"/> row.
/// Selected state is owned by the parent VM's
/// <see cref="PlaylistViewModel.SelectedSessionControlChip"/> property so it
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
