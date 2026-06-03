using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Windows.UI;

namespace Wavee.UI.WinUI.ViewModels.Playlist;

/// <summary>
/// Drives the PlaylistPage layout fork: <see cref="Banner"/> = full-width
/// hero image at top + two-column content below (editorial / radio
/// playlists with a <c>header_image_url_desktop</c>); <see cref="Cover"/>
/// = classic square cover in the left column + tracks on the right
/// (user-created playlists). See <see cref="LayoutMode"/> for the
/// detection chain (cache peek → ApplyDetail authoritative).
/// </summary>
public enum PlaylistLayoutMode { Banner, Cover }

/// <summary>
/// Owns the playlist "envelope" — name, description, owner, hero/cover/header
/// image, capabilities, palette, follower count, collaborator stack, invite
/// link, layout mode. Extracted from <c>PlaylistViewModel</c> so the metadata
/// surface can evolve independently of the track table and mutation surface.
///
/// <para>The header DOES NOT own the track list; it only observes a snapshot
/// accessor passed in from the parent so <see cref="ShouldShowAddedByColumn"/>
/// can compute the unique-contributor gate, and so
/// <see cref="RebuildCollaboratorsFromContext"/> can derive the avatar stack
/// from the <c>AddedBy</c> values present in already-loaded rows.</para>
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class PlaylistHeaderViewModel : ObservableObject
{
    private readonly IUserProfileResolver? _userProfileResolver;
    private readonly IPlaylistMutationService _playlistMutationService;
    private readonly IPlaylistPermissionService _playlistPermissionService;
    private readonly ILibraryDataService _libraryDataService;
    private readonly IAuthState? _authState;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<IReadOnlyList<PlaylistTrackDto>> _tracksSnapshotProvider;
    private readonly Func<string> _playlistIdProvider;

    private CancellationTokenSource? _followerCountCts;
    private CancellationTokenSource? _paletteCts;
    private string? _disposedGuard;
    private bool _disposed;

    public PlaylistHeaderViewModel(
        ILibraryDataService libraryDataService,
        IPlaylistMutationService playlistMutationService,
        IPlaylistPermissionService playlistPermissionService,
        IUserProfileResolver? userProfileResolver,
        IAuthState? authState,
        ILogger? logger,
        Func<IReadOnlyList<PlaylistTrackDto>> tracksSnapshotProvider,
        Func<string> playlistIdProvider)
    {
        _libraryDataService = libraryDataService;
        _playlistMutationService = playlistMutationService;
        _playlistPermissionService = playlistPermissionService;
        _userProfileResolver = userProfileResolver;
        _authState = authState;
        _logger = logger;
        _tracksSnapshotProvider = tracksSnapshotProvider;
        _playlistIdProvider = playlistIdProvider;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    // ── Playlist envelope (record) ───────────────────────────────────────────

    private PlaylistView? _playlist;
    public PlaylistView? Playlist
    {
        get => _playlist;
        private set
        {
            if (EqualityComparer<PlaylistView?>.Default.Equals(_playlist, value))
                return;

            var old = _playlist;
            _playlist = value;
            OnPropertyChanged(nameof(Playlist));

            var paletteChanged = !Equals(old?.Palette, value?.Palette);
            RaisePlaylistEnvelopeDependents();

            if (!string.Equals(old?.Name, value?.Name, StringComparison.Ordinal))
                LogPlaylistNameChanged(value?.Name ?? string.Empty);

            if (old?.IsCollaborative != value?.IsCollaborative)
                RebuildCollaboratorsFromContext();

            if (paletteChanged)
                ApplyTheme(_isDarkTheme);
        }
    }

    /// <summary>Direct setter used by the parent VM during full envelope swaps
    /// (Activate's clear-down branch). Bypasses the diff so callers can replace
    /// the reference even if the new instance equals the old.</summary>
    public void SetPlaylist(PlaylistView? value) => Playlist = value;

    public PlaylistView EmptyPlaylistEnvelope()
        => new(
            Id: PlaylistId,
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

    /// <summary>Mutates the envelope through a <c>with</c>-record update. Used by every
    /// property setter on this VM and by the parent's incremental detail-apply paths.</summary>
    public void UpdatePlaylist(Func<PlaylistView, PlaylistView> update)
    {
        Playlist = update(Playlist ?? EmptyPlaylistEnvelope());
    }

    private string PlaylistId => _playlistIdProvider() ?? string.Empty;

    private static readonly string[] PlaylistEnvelopeDependentProperties =
    [
        nameof(PlaylistName), nameof(PlaylistDescription), nameof(IsDescriptionViewerCardVisible),
        nameof(PlaylistImageUrl), nameof(HeaderImageUrl), nameof(HasHeaderImage),
        nameof(OwnerName), nameof(OwnerId), nameof(OwnerAvatarUrl), nameof(IsOwner),
        nameof(IsPublic), nameof(IsCollaborative), nameof(BasePermission),
        nameof(CanEditItems), nameof(CanShowRecommendations), nameof(CanAdministratePermissions), nameof(CanCancelMembership),
        nameof(CanAbuseReport), nameof(CanEditMetadata), nameof(CanEditName),
        nameof(CanEditDescription), nameof(CanEditPicture), nameof(CanEditCollaborative),
        nameof(CanDelete), nameof(HasOverflowItems),
        nameof(PlaylistFormatAttributes), nameof(IsChart), nameof(ChartHeaderLine),
        nameof(CurrentRevision), nameof(SessionControlGroupId), nameof(Palette)
    ];

    /// <summary>Fired after <see cref="Playlist"/> mutates so the parent can fan
    /// out to commands that gate on capabilities (
    /// <c>RemoveSelectedCommand.NotifyCanExecuteChanged</c>, etc.). Parent listens
    /// instead of having child VMs depend on each other directly.</summary>
    public event EventHandler? PlaylistEnvelopeChanged;

    private void RaisePlaylistEnvelopeDependents()
    {
        foreach (var propertyName in PlaylistEnvelopeDependentProperties)
            OnPropertyChanged(propertyName);

        PlaylistEnvelopeChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Envelope-projected properties ────────────────────────────────────────

    public string PlaylistName
    {
        get => Playlist?.Name ?? string.Empty;
        internal set => UpdatePlaylist(p => p with { Name = value ?? string.Empty });
    }

    public string? PlaylistDescription
    {
        get => Playlist?.Description;
        internal set => UpdatePlaylist(p => p with { Description = value });
    }

    public string? PlaylistImageUrl
    {
        get => Playlist?.ImageUrl;
        internal set => UpdatePlaylist(p => p with { ImageUrl = value });
    }

    public IReadOnlyDictionary<string, string>? PlaylistFormatAttributes => Playlist?.FormatAttributes;
    public bool IsChart => ChartPlaylistInfo.From(PlaylistFormatAttributes) is not null;
    public string ChartHeaderLine => BuildChartHeaderLine(PlaylistFormatAttributes);
    public byte[]? CurrentRevision => Playlist?.Revision;
    public string? SessionControlGroupId => Playlist?.SessionControlGroupId;

    public string? HeaderImageUrl
    {
        get => Playlist?.HeaderImageUrl;
        set => UpdatePlaylist(p => p with { HeaderImageUrl = value });
    }

    public string OwnerName
    {
        get => Playlist?.OwnerName ?? string.Empty;
        internal set => UpdatePlaylist(p => p with { OwnerName = value ?? string.Empty });
    }

    public bool IsOwner => Playlist?.IsOwner == true;

    public bool IsPublic
    {
        get => Playlist?.IsPublic == true;
        internal set => UpdatePlaylist(p => p with { IsPublic = value });
    }

    public bool IsCollaborative
    {
        get => Playlist?.IsCollaborative == true;
        internal set => UpdatePlaylist(p => p with { IsCollaborative = value });
    }

    public PlaylistBasePermission BasePermission => Playlist?.BasePermission ?? PlaylistBasePermission.Viewer;

    // Defensive OR with IsOwner mirrors the wire-layer logic in
    // LibraryDataService.MapCapabilities (CanEditItems = value.CanEditItems
    // || isOwner). In production we've seen Playlist envelopes land with
    // IsOwner=true but CanEditItems=false anyway — most likely a partial /
    // prefetched detail stamping the capabilities back to the ViewOnly
    // default after a full detail had already set them. Treating owners as
    // always able to edit items matches Spotify's actual permission model
    // and unblocks the owner-only Recommended Songs footer + remove gestures.
    public bool CanEditItems => Playlist?.CanEditItems == true || Playlist?.IsOwner == true;

    public bool CanAdministratePermissions => Playlist?.CanAdministratePermissions == true;
    public bool CanCancelMembership => Playlist?.CanCancelMembership == true;
    public bool CanAbuseReport => Playlist?.CanAbuseReport == true;
    public bool CanEditMetadata => Playlist?.CanEditMetadata == true;
    public bool CanEditName => Playlist?.CanEditName == true;
    public bool CanEditDescription => Playlist?.CanEditDescription == true;
    public bool CanEditPicture => Playlist?.CanEditPicture == true;
    public bool CanEditCollaborative => Playlist?.CanEditCollaborative == true;
    public bool CanDelete => Playlist?.CanDelete == true;

    public string? OwnerId
    {
        get => Playlist?.OwnerId;
        internal set => UpdatePlaylist(p => p with { OwnerId = value });
    }

    public string? OwnerAvatarUrl
    {
        get => Playlist?.OwnerAvatarUrl;
        internal set => UpdatePlaylist(p => p with { OwnerAvatarUrl = value });
    }

    /// <summary>True when the page should render the "…" overflow button — owners
    /// see Delete/Make-collaborative/Invite/Manage; collaborators see Leave.</summary>
    public bool HasOverflowItems =>
        CanDelete || CanEditCollaborative || CanCancelMembership || CanAdministratePermissions;

    /// <summary>
    /// True when the read-only description card should render — i.e. the user
    /// CANNOT edit the description (so we keep the existing RichTextBlock +
    /// hyperlink path) AND the playlist actually carries a description. Editors
    /// get the editable branch instead, which is always visible (it shows the
    /// placeholder when empty so they can add one).
    /// </summary>
    public bool IsDescriptionViewerCardVisible => !CanEditDescription && !string.IsNullOrEmpty(PlaylistDescription);

    /// <summary>
    /// True when the AddedBy column should render. Computed directly from the
    /// current track snapshot so it can't lag a stale <see cref="Collaborators"/>
    /// rebuild: we count distinct non-empty <c>AddedBy</c> values across the
    /// playlist's loaded tracks and require ≥2.
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
    public bool ShouldShowAddedByColumn => _shouldShowAddedByColumn;

    // ── Recommendations gate (read-only — Mutations owns the fetch) ──────────

    /// <summary>True when the recommendations footer should render: owner +
    /// at least one seed track. The actual count threshold (TotalTracks) is
    /// supplied by the parent via <see cref="SetTotalTracks"/> below — the
    /// header doesn't own the track count, but the gate has to combine the
    /// owner capability (header) with the track count (track list).</summary>
    public bool CanShowRecommendations => CanEditItems && _totalTracks > 0;

    private int _totalTracks;

    /// <summary>Parent forwards <c>TrackList.TotalTracks</c> updates here so the
    /// header's <see cref="CanShowRecommendations"/> gate stays in sync without
    /// the child VMs needing direct references to each other.</summary>
    public void SetTotalTracks(int value)
    {
        if (_totalTracks == value) return;
        _totalTracks = value;
        OnPropertyChanged(nameof(CanShowRecommendations));
        OnPropertyChanged(nameof(MetaInlineLine));
    }

    private string _totalDurationCached = string.Empty;

    /// <summary>Parent forwards <c>TrackList.TotalDuration</c> here so the
    /// <see cref="MetaInlineLine"/> aggregator can include the formatted duration
    /// in the dot-joined stats string.</summary>
    public void SetTotalDuration(string value)
    {
        if (string.Equals(_totalDurationCached, value, StringComparison.Ordinal)) return;
        _totalDurationCached = value ?? string.Empty;
        OnPropertyChanged(nameof(MetaInlineLine));
    }

    // ── Collaborator stack ───────────────────────────────────────────────────

    /// <summary>Resolved collaborator list. Populated by
    /// <see cref="RebuildCollaboratorsFromContext"/> from the playlist's owner +
    /// the unique <c>AddedBy</c> users discovered in the track list. The
    /// stubbed members backend isn't a source of truth here — we derive purely
    /// from data that's already on screen.</summary>
    public ObservableCollection<PlaylistMemberResult> Collaborators { get; } = new();

    // Signature of the last-rendered Collaborators set. Skips redundant rebuilds
    // when ApplyDetail / LoadTracksAsync / ResolveAddedByUsernamesAsync re-enter
    // RebuildCollaboratorsFromContext with the same membership; without this
    // guard the page rebuilds the avatar stack on every fire and a fresh nav
    // can trigger 3+ rebuilds back-to-back on a large playlist.
    private string? _lastCollaboratorSignature;
    private bool _shouldShowAddedByColumn;

    private readonly Dictionary<string, UserProfileSummary> _addedByProfilesById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _addedByProfilesGate = new();

    [ObservableProperty]
    public partial bool HasCollaborators { get; set; }

    /// <summary>Most recently generated invite link for "Invite collaborators…".
    /// Cleared on playlist swap.</summary>
    [ObservableProperty]
    public partial PlaylistInviteLink? LatestInviteLink { get; set; }

    /// <summary>Bare current-user id, used to suppress the "added by" badge on
    /// rows the current user added themselves.</summary>
    public string? CurrentUserId => _authState?.Username;

    public bool TryGetAddedByProfile(string addedBy, out UserProfileSummary? profile)
    {
        if (string.IsNullOrWhiteSpace(addedBy))
        {
            profile = null;
            return false;
        }

        lock (_addedByProfilesGate)
            return _addedByProfilesById.TryGetValue(addedBy, out profile);
    }

    /// <summary>
    /// Resolves display name + avatar for every distinct <c>AddedBy</c> on the
    /// current playlist (including the current user, who shows up in the
    /// collaborator-mode AddedBy column too), and writes the results back into
    /// the <c>PlaylistTrackDto</c> instances. Each successful write fires
    /// PropertyChanged on the affected DTO so already-realized cells re-render
    /// without a full grid rebuild.
    /// </summary>
    public async Task ResolveAddedByUsernamesAsync(string forPlaylistId, CancellationToken ct)
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
        var snapshot = _tracksSnapshotProvider();
        var unique = snapshot
            .Select(t => t.AddedBy)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger?.LogInformation(
            "[addedby] resolve start for '{Id}' — uniqueCount={N} unique=[{List}]",
            forPlaylistId, unique.Count, string.Join(",", unique));

        if (unique.Count == 0) return;

        var missing = unique
            .Where(id => !TryGetAddedByProfile(id!, out _))
            .ToList();

        if (missing.Count == 0)
        {
            _logger?.LogInformation("[addedby] all profiles already cached for '{Id}'", forPlaylistId);
            return;
        }

        var lookup = new Dictionary<string, UserProfileSummary?>(StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(missing.Select(async id =>
        {
            try
            {
                var profile = await _userProfileResolver.GetProfileAsync(id!, ct).ConfigureAwait(false);
                lock (lookup) lookup[id!] = profile;
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
        lock (_addedByProfilesGate)
        {
            foreach (var (id, profile) in lookup)
            {
                if (profile is null)
                {
                    skippedNullProfile++;
                    continue;
                }

                if (_addedByProfilesById.TryGetValue(id, out var existing) &&
                    string.Equals(existing.DisplayName, profile.DisplayName, StringComparison.Ordinal) &&
                    string.Equals(existing.AvatarUrl, profile.AvatarUrl, StringComparison.Ordinal))
                {
                    continue;
                }

                _addedByProfilesById[id] = profile;
                anyChanged++;
            }
        }

        _logger?.LogInformation(
            "[addedby] profile cache update complete for '{Id}': changed={Changed} skippedNullProfile={Nul}",
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

    /// <summary>
    /// Builds the collaborator list shown in the hero avatar stack from data
    /// already on screen — the playlist owner plus the unique <c>AddedBy</c>
    /// users discovered across the current track snapshot. Independent of the
    /// stubbed members backend (<see cref="LoadCollaboratorsAsync"/>) so the
    /// stack works on any playlist with multiple contributors, not just ones
    /// the current user can administrate.
    ///
    /// Visibility rule: stack is shown when the playlist is open for collab
    /// (<see cref="IsCollaborative"/>) OR when ≥2 unique contributors are
    /// present. Single-owner non-collab playlists collapse to nothing.
    /// </summary>
    public void RebuildCollaboratorsFromContext()
    {
        // Snapshot the track list — this method is dispatcher-thread but the
        // parent reassigns its backing field on the same thread, so a local
        // read keeps the dedupe stable.
        var tracks = _tracksSnapshotProvider();
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
        var addedBySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tracks)
        {
            var addedBy = t.AddedBy;
            if (string.IsNullOrEmpty(addedBy)) continue;
            addedBySeen.Add(addedBy);
            if (!seen.Add(addedBy)) continue;
            TryGetAddedByProfile(addedBy, out var profile);

            members.Add(new PlaylistMemberResult
            {
                UserId = addedBy,
                Username = addedBy,
                DisplayName = string.IsNullOrWhiteSpace(profile?.DisplayName) ? null : profile.DisplayName,
                AvatarUrl = profile?.AvatarUrl,
                Role = PlaylistMemberRole.Contributor,
            });
        }

        SetShouldShowAddedByColumn(addedBySeen.Count >= 2);

        // Skip the rebuild entirely if the resolved set hasn't actually
        // changed — large playlists otherwise pay for 3+ stack-visual rebuilds
        // on a fresh nav (ApplyDetail → LoadTracksAsync → ResolveAddedBy…).
        // Signature includes display name + avatar URL so the resolved-names
        // pass still fires when those fields update without an id change.
        var signature = BuildCollaboratorSignature(members, IsCollaborative);
        if (string.Equals(signature, _lastCollaboratorSignature, StringComparison.Ordinal))
            return;
        _lastCollaboratorSignature = signature;

        // Single Reset event (via the ObservableCollection.ReplaceWith extension)
        // instead of Clear + N Adds — the page's CollectionChanged handler runs
        // a full visual rebuild on every event, so collapsing N+1 events to 1
        // saves the same multiplier in synchronous UI work.
        Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(Collaborators, members);

        HasCollaborators = IsCollaborative || Collaborators.Count >= 2;

        _logger?.LogInformation(
            "[collab-stack] rebuilt: count={Count} hasCollab={Has} isCollab={IsCollab} ownerId={Owner}",
            Collaborators.Count, HasCollaborators, IsCollaborative, ownerId);

        _logger?.LogInformation(
            "[addedby-gate] '{Id}' uniqueAddedBys={N} → ShouldShow={Show}",
            PlaylistId, addedBySeen.Count, ShouldShowAddedByColumn);
    }

    /// <summary>
    /// Re-derive the <see cref="ShouldShowAddedByColumn"/> gate (raises a
    /// PropertyChanged) — used by the parent after the track snapshot is
    /// reassigned so the OneWay binding picks up the new value even when the
    /// resolved collaborator signature didn't change.
    /// </summary>
    public void NotifyAddedByGateChanged()
        => RebuildCollaboratorsFromContext();

    public void RefreshTrackDerivedState()
        => RebuildCollaboratorsFromContext();

    private void SetShouldShowAddedByColumn(bool value)
    {
        if (_shouldShowAddedByColumn == value)
            return;

        _shouldShowAddedByColumn = value;
        OnPropertyChanged(nameof(ShouldShowAddedByColumn));
    }

    private static string ExtractBareUserId(string idOrUri)
    {
        const string prefix = "spotify:user:";
        return idOrUri.StartsWith(prefix, StringComparison.Ordinal)
            ? idOrUri[prefix.Length..]
            : idOrUri;
    }

    /// <summary>
    /// Compact signature for the collaborator set so RebuildCollaboratorsFromContext
    /// can short-circuit when the resolved members are identical to the last
    /// applied set. Includes UserId, DisplayName, and AvatarUrl so the
    /// ResolveAddedByUsernamesAsync pass (which updates names/avatars on the
    /// same id set) still produces a different signature and fires a rebuild.
    /// IsCollaborative is part of the signature too so the "Open to
    /// collaboration" trailing label flips when the playlist mode changes.
    /// </summary>
    private static string BuildCollaboratorSignature(
        IReadOnlyList<PlaylistMemberResult> members, bool isCollaborative)
    {
        var sb = new System.Text.StringBuilder(64 + members.Count * 32);
        sb.Append(isCollaborative ? '1' : '0').Append('|').Append(members.Count).Append('|');
        foreach (var m in members)
        {
            sb.Append(m.UserId ?? string.Empty).Append('\x1F')
              .Append(m.DisplayName ?? string.Empty).Append('\x1F')
              .Append(m.AvatarUrl ?? string.Empty).Append('\x1E');
        }
        return sb.ToString();
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
            var raw = await _playlistPermissionService
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

            // Only replace the seeded (context-rebuild) chip strip when the
            // backend actually returned something — otherwise we'd erase the
            // owner + addedBy fallback for users who lack permission to view
            // the real member list.
            if (enriched.Length > 0)
            {
                Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(Collaborators, enriched);
                HasCollaborators = Collaborators.Count > 0;
            }
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
            await _playlistPermissionService
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
            await _playlistPermissionService
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
            LatestInviteLink = await _playlistPermissionService
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
            await _playlistPermissionService
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

    // ── Follower count ───────────────────────────────────────────────────────

    [ObservableProperty]
    public partial int FollowerCount { get; set; }

    /// <summary>
    /// True while the popcount fetch for the current playlist is in flight.
    /// Drives a shimmer placeholder under the title; goes false on success
    /// (whether the count came back as 0 or a real number) or on cancellation.
    /// </summary>
    [ObservableProperty]
    public partial bool IsFollowerCountLoading { get; set; }

    /// <summary>
    /// Formatted follower count.
    /// </summary>
    public string FollowerCountFormatted => FollowerCount <= 0
        ? string.Empty
        : AppLocalization.Format("Playlist_FollowerCount", Wavee.UI.Formatters.NumberFormatter.FormatFollowerCount(FollowerCount));

    /// <summary>
    /// Visibility gate for the resolved follower-count text. False while the
    /// popcount fetch is in flight (the shimmer takes the slot) and false when
    /// the playlist has no followers / hides its count (slot collapses entirely).
    /// </summary>
    public bool ShowFollowerCountText
        => !IsFollowerCountLoading && !string.IsNullOrEmpty(FollowerCountFormatted);

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
            if (_totalTracks > 0)
                parts.Add(_totalTracks == 1
                    ? AppLocalization.GetString("Count_Song_One")
                    : AppLocalization.Format("Count_Song_Many", _totalTracks));
            if (!string.IsNullOrWhiteSpace(_totalDurationCached))
                parts.Add(_totalDurationCached);
            if (!string.IsNullOrWhiteSpace(FollowerCountFormatted))
                parts.Add(FollowerCountFormatted);
            return string.Join(" · ", parts);
        }
    }

    partial void OnFollowerCountChanged(int value)
    {
        OnPropertyChanged(nameof(FollowerCountFormatted));
        OnPropertyChanged(nameof(ShowFollowerCountText));
        OnPropertyChanged(nameof(MetaInlineLine));
    }

    partial void OnIsFollowerCountLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFollowerCountText));
    }

    /// <summary>
    /// Background follower-count fetch. Held out of the main detail-load path so
    /// the playlist UI renders immediately and the count chip shimmers in
    /// asynchronously when the popcount endpoint replies.
    /// </summary>
    public async Task LoadFollowerCountAsync(string playlistId)
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

    // ── Palette ──────────────────────────────────────────────────────────────
    //
    // Mirrors AlbumViewModel's palette pipeline: fetched via Pathfinder's
    // fetchPlaylist persisted query, applied per-theme on load and on
    // ActualThemeChanged. Same brush set as the album page so the two
    // surfaces feel like siblings.

    public AlbumPalette? Palette => Playlist?.Palette;
    private bool _isDarkTheme;

    /// <summary>Subtle page-wash brush tinted toward the playlist's color. Null when no palette.</summary>
    /// <remarks>No longer bound by PlaylistPage — the redesign drops the page-wide
    /// wash so it doesn't bleed across the track table. Property kept so the
    /// existing palette pipeline still computes (Album/Show/Concert/Episode pages
    /// share the same shape) and so callers don't break if the binding returns.</remarks>
    [ObservableProperty]
    public partial Brush? PaletteBackdropBrush { get; set; }

    /// <summary>Hero gradient brush — palette-tinted left-to-right band, theme-aware alpha.</summary>
    [ObservableProperty]
    public partial Brush? PaletteHeroGradientBrush { get; set; }

    /// <summary>Accent pill background brush. Null falls back to system accent.</summary>
    [ObservableProperty]
    public partial Brush? PaletteAccentPillBrush { get; set; }

    /// <summary>Accent pill foreground brush — auto-computed from accent luminance.</summary>
    [ObservableProperty]
    public partial Brush? PaletteAccentPillForegroundBrush { get; set; }

    // ── Banner colors (no-image hero fallback) ────────────────────────────
    // When the playlist has no header_image_url_desktop, the hero banner is
    // painted by AnimatedHeroBackground (Win2D + MeshGradientShader) seeded
    // from the cover-extracted palette. PrimaryColor/AccentColor below feed
    // that control's two DPs. Computed in ApplyTheme; null fallback uses
    // the system accent so cold-start (no palette yet) still renders something.

    /// <summary>Banner fallback primary color — feeds AnimatedHeroBackground.PrimaryColor
    /// when HeaderImageUrl is null. Sourced from AlbumPalette tier Background.</summary>
    [ObservableProperty]
    public partial Color BannerPrimaryColor { get; set; } = Color.FromArgb(255, 90, 50, 160);

    /// <summary>Banner fallback accent color — feeds AnimatedHeroBackground.AccentColor
    /// when HeaderImageUrl is null. Sourced from AlbumPalette tier BackgroundTinted.</summary>
    [ObservableProperty]
    public partial Color BannerAccentColor { get; set; } = Color.FromArgb(255, 36, 198, 220);

    /// <summary>True when the playlist has a header image; used to route the banner
    /// row between the composition image surface and the AnimatedHeroBackground
    /// fallback. Mirrors HeaderImageUrl != null with a Bool shape so XAML can
    /// bind both sides via BoolToVisibilityConverter without a custom converter.</summary>
    public bool HasHeaderImage => !string.IsNullOrEmpty(HeaderImageUrl);

    /// <summary>
    /// Drives the page-level layout fork: editorial / radio playlists with a
    /// header image render the banner-style hero (full-width image at top,
    /// title overlaid, two-column content below); user-created playlists
    /// render the classic cover-in-left-column layout.
    /// </summary>
    [ObservableProperty]
    public partial PlaylistLayoutMode LayoutMode { get; set; } = PlaylistLayoutMode.Cover;

    /// <summary>
    /// Background palette fetch via Pathfinder's fetchPlaylist persisted query.
    /// Runs in parallel with the main detail load so the hero starts in a
    /// neutral state and tints in once the colour set lands. Mirrors
    /// AlbumViewModel's palette pipeline so the two surfaces look like siblings.
    /// </summary>
    public async Task LoadPaletteAsync(string playlistId)
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
                UpdatePlaylist(p => p with { Palette = palette });
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

        var tier = Palette is null
            ? null
            : (isDark
                ? (Palette.HigherContrast ?? Palette.HighContrast)
                : (Palette.HighContrast ?? Palette.HigherContrast));

        if (tier == null)
        {
            PaletteBackdropBrush = null;
            PaletteHeroGradientBrush = null;
            PaletteAccentPillBrush = null;
            PaletteAccentPillForegroundBrush = null;
            // Banner falls back to system-accent-ish defaults when no palette
            // is available yet — cold start before LoadPaletteAsync resolves.
            BannerPrimaryColor = Color.FromArgb(255, 90, 50, 160);
            BannerAccentColor = Color.FromArgb(255, 36, 198, 220);
            return;
        }

        var bg = Color.FromArgb(255, tier.BackgroundR, tier.BackgroundG, tier.BackgroundB);
        var bgTint = Color.FromArgb(255, tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);
        // Lifted accent base — same shape as Artist/Show. Replaces raw TextAccent
        // (which was ≈ Spotify green for most playlists) so the play button reads
        // as part of the cover-derived identity in both Light and Dark.
        var accentBase = TintColorHelper.BrightenForTint(bgTint, targetMax: 210);

        // Light mode: blend palette colors toward white before applying alpha so
        // dark covers don't drag the page dark. Dark mode unchanged.
        var heroBg     = isDark ? bg     : TintColorHelper.LightTint(bg);
        var heroBgTint = isDark ? bgTint : TintColorHelper.LightTint(bgTint);
        var washColor  = isDark ? bg     : TintColorHelper.LightTint(bg);

        // Banner colors — feed AnimatedHeroBackground when the playlist has no
        // header_image_url_desktop. Use the saturated tier values directly
        // (not the LightTint blend used for the wash) so the procedural mesh
        // gradient stays visually punchy regardless of theme.
        BannerPrimaryColor = bg;
        BannerAccentColor = bgTint;

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

    // ── Recomputed projections ──────────────────────────────────────────────

    private static string BuildChartHeaderLine(IReadOnlyDictionary<string, string>? formatAttributes)
    {
        var info = ChartPlaylistInfo.From(formatAttributes);
        if (info is null)
            return string.Empty;

        var parts = new List<string>(2);
        if (info.LastUpdated is { } when)
            parts.Add(AppLocalization.Format(
                "Playlist_Chart_Updated", when.ToLocalTime().ToString("MMM d")));
        if (info.NewEntriesCount is > 0 and var n)
            parts.Add(n == 1
                ? AppLocalization.GetString("Playlist_Chart_NewEntriesOne")
                : AppLocalization.Format("Playlist_Chart_NewEntriesMany", n));
        return string.Join(" · ", parts);
    }

    // ── Follow toggle ───────────────────────────────────────────────────────

    /// <summary>True if the playlist is in the current user's library/rootlist (heart
    /// filled). Seeded on navigation by <see cref="RefreshFollowedStateAsync"/> and
    /// toggled by <see cref="ToggleFollowAsync"/>.</summary>
    [ObservableProperty]
    public partial bool IsFollowed { get; set; }

    /// <summary>
    /// Toggles whether the playlist is in the current user's library. The visual flip
    /// is optimistic and reverted if the rootlist write (<c>SetPlaylistFollowedAsync</c>)
    /// fails.
    /// </summary>
    [RelayCommand]
    private async Task ToggleFollowAsync()
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;
        var nextValue = !IsFollowed;
        IsFollowed = nextValue;
        try
        {
            await _playlistMutationService.SetPlaylistFollowedAsync(PlaylistId, nextValue)
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
    /// Seeds <see cref="IsFollowed"/> from the user's library/rootlist membership so the
    /// heart reflects saved-state on navigation (it is otherwise only written by
    /// <see cref="ToggleFollowAsync"/>, leaving an already-saved playlist showing as
    /// unsaved). Cache-first to avoid a network hop on every nav; bails if the user
    /// navigated to a different playlist while the lookup was in flight.
    /// </summary>
    public async Task RefreshFollowedStateAsync(string playlistId)
    {
        if (string.IsNullOrEmpty(playlistId)) return;
        try
        {
            var list = await _libraryDataService.TryGetUserPlaylistsFromCacheAsync().ConfigureAwait(true)
                       ?? await _libraryDataService.GetUserPlaylistsAsync().ConfigureAwait(true);
            if (!string.Equals(PlaylistId, playlistId, StringComparison.Ordinal)) return;

            static string BareId(string uri)
            {
                var i = uri.LastIndexOf(':');
                return i >= 0 ? uri[(i + 1)..] : uri;
            }
            var target = BareId(playlistId);
            IsFollowed = list.Any(p => string.Equals(BareId(p.Id), target, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "RefreshFollowedStateAsync failed for {PlaylistId}", playlistId);
        }
    }

    /// <summary>
    /// Opens the playlist owner's profile page. No-op when <see cref="OwnerId"/>
    /// is missing.
    /// </summary>
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

    // ── Logging helper ──────────────────────────────────────────────────────

    // Caller-resolving log helper for diagnosing "who's flipping PlaylistName?"
    // bugs. Uses StackFrame.GetMethod which the trim analyzer can't reason about
    // (metadata can be incomplete under trimming, hence the IL2026 escalation
    // when WarningsAsErrors covers it). The helper is dev-time diagnostic only —
    // suppressed at the source rather than refactored because the caller's
    // identity is exactly what we want and there is no AOT-safe equivalent in
    // System.Diagnostics today.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Diagnostic logging only; missing caller info under trim is acceptable.")]
    private void LogPlaylistNameChanged(string value)
    {
        // First two stack frames are this method + the property setter; the third is the caller.
        var stack = new System.Diagnostics.StackTrace(skipFrames: 1, fNeedFileInfo: false);
        var caller = stack.FrameCount > 1 ? stack.GetFrame(1)?.GetMethod() : null;
        _logger?.LogDebug(
            "PlaylistName -> '{Value}' (PlaylistId='{PlaylistId}', Caller={Caller})",
            value, PlaylistId,
            caller is null ? "<unknown>" : $"{caller.DeclaringType?.Name}.{caller.Name}");
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>Resets transient state that should not survive a playlist swap.
    /// Called by the parent's Activate() during the isNewPlaylist branch.</summary>
    public void ResetForNewPlaylist()
    {
        _lastCollaboratorSignature = null;
        SetShouldShowAddedByColumn(false);
        // Resilient reset during navigation (issue #6) — raw Clear() on the bound Collaborators
        // list mid-layout throws COMException E_FAIL (the same hazard Dispose() below documents).
        Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(Collaborators, []);
        HasCollaborators = false;
        LatestInviteLink = null;
        PaletteBackdropBrush = null;
        PaletteHeroGradientBrush = null;
        PaletteAccentPillBrush = null;
        PaletteAccentPillForegroundBrush = null;
        FollowerCount = 0;
        IsFollowerCountLoading = false;
        _totalTracks = 0;
        _totalDurationCached = string.Empty;
        OnPropertyChanged(nameof(MetaInlineLine));
        OnPropertyChanged(nameof(CanShowRecommendations));
    }

    /// <summary>Cancels in-flight background work for this header. Called by the
    /// parent on Deactivate / Hibernate / Dispose.</summary>
    public void Deactivate()
    {
        _followerCountCts?.Cancel();
        _followerCountCts?.Dispose();
        _followerCountCts = null;
        _paletteCts?.Cancel();
        _paletteCts?.Dispose();
        _paletteCts = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Deactivate();
        // Do not clear Collaborators here. During app shutdown the DI host can
        // dispose this VM before XAML pages have detached their collection
        // handlers; raising CollectionChanged then can touch already-torn-down
        // UIElementCollection instances.
    }
}
