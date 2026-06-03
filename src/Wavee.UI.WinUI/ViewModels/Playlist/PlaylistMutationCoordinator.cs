using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.ViewModels.Playlist;

/// <summary>
/// Owns the mutation surface of the playlist page — rename, description
/// update, cover photo change/remove, delete, collaborative toggle,
/// recommendations fetch/append, and the capability-gated CanExecute
/// notifications that drive command enabled state.
///
/// <para>Stateful only in the recommendations cache + the in-flight flags
/// (IsRenaming / IsUpdatingDescription / IsUploadingCover /
/// IsFetchingRecommendations). All other state (PlaylistId, capabilities,
/// current track snapshot) is pulled in via the accessor delegates the
/// parent supplies — those values live on the sibling VMs.</para>
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class PlaylistMutationCoordinator : ObservableObject
{
    private readonly IPlaylistMutationService _playlistMutationService;
    private readonly IPlaylistPermissionService _playlistPermissionService;
    private readonly ILogger? _logger;

    // ── Bound providers — pulled from sibling VMs / the parent ───────────────

    private readonly Func<string> _playlistIdProvider;
    private readonly Func<string?> _playlistNameProvider;
    private readonly Action<string?> _playlistNameSetter;
    private readonly Func<string?> _playlistDescriptionProvider;
    private readonly Action<string?> _playlistDescriptionSetter;
    private readonly Func<bool> _isCollaborativeProvider;
    private readonly Action<bool> _isCollaborativeSetter;
    private readonly Func<bool> _isPublicProvider;
    private readonly Action<bool> _isPublicSetter;
    private readonly Func<bool> _isOwnerProvider;
    private readonly Func<bool> _canEditNameProvider;
    private readonly Func<bool> _canEditDescriptionProvider;
    private readonly Func<bool> _canEditPictureProvider;
    private readonly Func<bool> _canEditCollaborativeProvider;
    private readonly Func<bool> _canEditItemsProvider;
    private readonly Func<bool> _canDeleteProvider;
    private readonly Func<int> _totalTracksProvider;
    private readonly Func<IReadOnlyList<PlaylistTrackDto>> _tracksSnapshotProvider;

    public PlaylistMutationCoordinator(
        IPlaylistMutationService playlistMutationService,
        IPlaylistPermissionService playlistPermissionService,
        ILogger? logger,
        Func<string> playlistIdProvider,
        Func<string?> playlistNameProvider,
        Action<string?> playlistNameSetter,
        Func<string?> playlistDescriptionProvider,
        Action<string?> playlistDescriptionSetter,
        Func<bool> isCollaborativeProvider,
        Action<bool> isCollaborativeSetter,
        Func<bool> isPublicProvider,
        Action<bool> isPublicSetter,
        Func<bool> isOwnerProvider,
        Func<bool> canEditNameProvider,
        Func<bool> canEditDescriptionProvider,
        Func<bool> canEditPictureProvider,
        Func<bool> canEditCollaborativeProvider,
        Func<bool> canEditItemsProvider,
        Func<bool> canDeleteProvider,
        Func<int> totalTracksProvider,
        Func<IReadOnlyList<PlaylistTrackDto>> tracksSnapshotProvider)
    {
        _playlistMutationService = playlistMutationService;
        _playlistPermissionService = playlistPermissionService;
        _logger = logger;
        _playlistIdProvider = playlistIdProvider;
        _playlistNameProvider = playlistNameProvider;
        _playlistNameSetter = playlistNameSetter;
        _playlistDescriptionProvider = playlistDescriptionProvider;
        _playlistDescriptionSetter = playlistDescriptionSetter;
        _isCollaborativeProvider = isCollaborativeProvider;
        _isCollaborativeSetter = isCollaborativeSetter;
        _isPublicProvider = isPublicProvider;
        _isPublicSetter = isPublicSetter;
        _isOwnerProvider = isOwnerProvider;
        _canEditNameProvider = canEditNameProvider;
        _canEditDescriptionProvider = canEditDescriptionProvider;
        _canEditPictureProvider = canEditPictureProvider;
        _canEditCollaborativeProvider = canEditCollaborativeProvider;
        _canEditItemsProvider = canEditItemsProvider;
        _canDeleteProvider = canDeleteProvider;
        _totalTracksProvider = totalTracksProvider;
        _tracksSnapshotProvider = tracksSnapshotProvider;
    }

    private string PlaylistId => _playlistIdProvider() ?? string.Empty;

    // ── Capability gate proxies (read from the header) ───────────────────────

    public bool CanEditName => _canEditNameProvider();
    public bool CanEditDescription => _canEditDescriptionProvider();
    public bool CanEditPicture => _canEditPictureProvider();
    public bool CanEditCollaborative => _canEditCollaborativeProvider();
    public bool CanEditItems => _canEditItemsProvider();
    public bool CanDelete => _canDeleteProvider();

    /// <summary>Owner-only: only the playlist owner can change its visibility.</summary>
    public bool CanChangeVisibility => _isOwnerProvider();

    /// <summary>
    /// Fan out CanExecute notifications to every capability-gated command on
    /// this VM. Called by the parent whenever the header's envelope (and thus
    /// the capability bits) changes.
    /// </summary>
    public void NotifyPlaylistCapabilityCommandsChanged()
    {
        FindRecommendedTracksCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        UpdateDescriptionCommand.NotifyCanExecuteChanged();
        ChangeCoverCommand.NotifyCanExecuteChanged();
        RemoveCoverCommand.NotifyCanExecuteChanged();
        DeletePlaylistCommand.NotifyCanExecuteChanged();
        ToggleCollaborativeCommand.NotifyCanExecuteChanged();
        ToggleVisibilityCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(CanEditName));
        OnPropertyChanged(nameof(CanEditDescription));
        OnPropertyChanged(nameof(CanEditPicture));
        OnPropertyChanged(nameof(CanEditCollaborative));
        OnPropertyChanged(nameof(CanEditItems));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanChangeVisibility));
    }

    // ── Inline edit (rename + description) ───────────────────────────────────

    /// <summary>True while a metadata edit (rename or description) is being saved.
    /// The view binds this to the InlineEditableText's IsBusy spinner.</summary>
    [ObservableProperty] public partial bool IsRenaming { get; set; }

    [ObservableProperty] public partial bool IsUpdatingDescription { get; set; }

    /// <summary>
    /// Optimistically sets the playlist name and persists via
    /// <see cref="IPlaylistMutationService.RenamePlaylistAsync"/>. On
    /// failure the previous name is restored and a toast is shown.
    /// Trims whitespace; rejects empty names (silent revert).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditName))]
    private async Task RenameAsync(string newName)
    {
        var playlistId = PlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;

        var trimmed = newName?.Trim() ?? string.Empty;
        var current = _playlistNameProvider() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, current, StringComparison.Ordinal))
            return;

        var previous = current;
        _playlistNameSetter(trimmed);
        IsRenaming = true;
        try
        {
            await _playlistMutationService.RenamePlaylistAsync(playlistId, trimmed).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RenameAsync failed for playlist '{Id}'; reverting", playlistId);
            _playlistNameSetter(previous);
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
    [ObservableProperty] public partial bool IsUploadingCover { get; set; }

    /// <summary>
    /// Persists a freshly-picked cover image. <paramref name="jpegBytes"/> must
    /// already be a JPEG ≤256 KB (use <c>PlaylistCoverHelper</c>). On failure
    /// the page reverts the local preview and shows a toast; on success the
    /// stored URL refreshes from the next AlbumStore push.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditPicture))]
    private async Task ChangeCoverAsync(byte[] jpegBytes)
    {
        var playlistId = PlaylistId;
        if (string.IsNullOrEmpty(playlistId) || jpegBytes is null || jpegBytes.Length == 0)
            return;

        IsUploadingCover = true;
        try
        {
            await _playlistMutationService.UpdatePlaylistCoverAsync(playlistId, jpegBytes).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ChangeCoverAsync failed for playlist '{Id}'", playlistId);
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
        var playlistId = PlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;

        try
        {
            await _playlistMutationService.DeletePlaylistAsync(playlistId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DeletePlaylistAsync failed for playlist '{Id}'", playlistId);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't delete playlist", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>
    /// Toggles the playlist between owner-only and collaborative. Optimistically
    /// flips the header's <c>IsCollaborative</c>; reverts on failure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditCollaborative))]
    private async Task ToggleCollaborativeAsync()
    {
        var playlistId = PlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;

        var previous = _isCollaborativeProvider();
        var next = !previous;
        _isCollaborativeSetter(next);
        try
        {
            await _playlistPermissionService.SetPlaylistCollaborativeAsync(playlistId, next).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ToggleCollaborativeAsync failed for playlist '{Id}'; reverting", playlistId);
            _isCollaborativeSetter(previous);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't update sharing setting", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>
    /// Optimistically flips the playlist's visibility (public ↔ private) and
    /// persists via the mutation service (base permission VIEWER/BLOCKED + the
    /// rootlist <c>public</c> flag). Owner-only; reverts on failure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeVisibility))]
    private async Task ToggleVisibilityAsync()
    {
        var playlistId = PlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;

        var previous = _isPublicProvider();
        var next = !previous;
        _isPublicSetter(next);
        try
        {
            await _playlistMutationService.SetPlaylistVisibilityAsync(playlistId, next).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ToggleVisibilityAsync failed for playlist '{Id}'; reverting", playlistId);
            _isPublicSetter(previous);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't update playlist visibility", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>
    /// Removes the custom cover and reverts to the auto-generated mosaic.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditPicture))]
    private async Task RemoveCoverAsync()
    {
        var playlistId = PlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;

        IsUploadingCover = true;
        try
        {
            await _playlistMutationService.RemovePlaylistCoverAsync(playlistId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RemoveCoverAsync failed for playlist '{Id}'", playlistId);
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
    /// Optimistically sets the playlist description and persists. Empty string
    /// clears the description on the server. On failure the previous value is
    /// restored.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditDescription))]
    private async Task UpdateDescriptionAsync(string newDescription)
    {
        var playlistId = PlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;

        var value = newDescription ?? string.Empty;
        var currentDescription = _playlistDescriptionProvider();
        if (string.Equals(value, currentDescription ?? string.Empty, StringComparison.Ordinal))
            return;

        var previous = currentDescription;
        _playlistDescriptionSetter(value);
        IsUpdatingDescription = true;
        try
        {
            await _playlistMutationService.UpdatePlaylistDescriptionAsync(playlistId, value).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "UpdateDescriptionAsync failed for playlist '{Id}'; reverting", playlistId);
            _playlistDescriptionSetter(previous);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't update description", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
        finally
        {
            IsUpdatingDescription = false;
        }
    }

    // ── Recommended Songs ("Enhance") ─────────────────────────────────────
    //
    // Owner-gated fetch of Spotify-curated track recommendations for THIS
    // playlist. Backed by /playlistextender/extendp/ via
    // IPlaylistMutationService.GetPlaylistRecommendationsAsync. The recommender
    // uses the playlist's existing tracks as seed, so the footer is hidden
    // (and never fetched) when the playlist is empty.
    //
    // Fetch is auto-triggered the first time both CanEditItems and
    // TotalTracks > 0 hold for a given PlaylistId — the user explicitly
    // wanted this surface to populate without a manual click. Refreshes
    // stay manual via the "Refresh" chip.

    private readonly ObservableCollection<RecommendedTrackResult> _recommendedTracks = [];
    public IReadOnlyList<RecommendedTrackResult> RecommendedTracks => _recommendedTracks;

    /// <summary>One-shot guard so capability + track-load events don't both
    /// trigger a fetch on the same activation. Reset on playlist switch.</summary>
    private string? _recommendationsAutoTriggeredFor;

    [ObservableProperty]
    public partial bool HasRecommendedTracks { get; set; }

    [ObservableProperty]
    public partial bool IsFetchingRecommendations { get; set; }

    /// <summary>True when the last recommendations fetch attempt threw. Drives
    /// the error card in the footer. Reset at the start of each new attempt.</summary>
    [ObservableProperty]
    public partial bool RecommendationsLoadFailed { get; set; }

    /// <summary>
    /// Re-evaluates the auto-trigger. Called whenever the track count or the
    /// capability gates change.
    /// </summary>
    public void MaybeAutoLoadRecommendations()
    {
        var playlistId = PlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;
        if (!CanEditItems || _totalTracksProvider() <= 0) return;
        if (_tracksSnapshotProvider().Count == 0) return;
        if (IsFetchingRecommendations) return;
        if (string.Equals(_recommendationsAutoTriggeredFor, playlistId, StringComparison.Ordinal)) return;

        _recommendationsAutoTriggeredFor = playlistId;
        FindRecommendedTracksCommand.Execute(null);
    }

    /// <summary>
    /// Fetches Spotify-recommended tracks to add to this playlist. Skip-list
    /// is seeded from the current playlist's track URIs so the server doesn't
    /// return tracks the user already has. On failure flips
    /// <see cref="RecommendationsLoadFailed"/> so the error card renders.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditItems))]
    private async Task FindRecommendedTracksAsync()
    {
        var playlistId = PlaylistId;
        if (string.IsNullOrEmpty(playlistId) || IsFetchingRecommendations) return;

        IsFetchingRecommendations = true;
        RecommendationsLoadFailed = false;
        try
        {
            var skipUris = _tracksSnapshotProvider()
                .Select(t => t.Uri)
                .Where(u => !string.IsNullOrEmpty(u))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var recs = await _playlistMutationService.GetPlaylistRecommendationsAsync(playlistId, skipUris, numResults: 20)
                .ConfigureAwait(true);

            Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(_recommendedTracks, recs);
            HasRecommendedTracks = _recommendedTracks.Count > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FindRecommendedTracksAsync failed for {PlaylistId}", playlistId);
            RecommendationsLoadFailed = true;
            // Keep _recommendedTracks intact — if a prior fetch succeeded, the
            // error card sits alongside the existing list rather than wiping it.
        }
        finally
        {
            IsFetchingRecommendations = false;
        }
    }

    /// <summary>Appends a single recommended track to the playlist. The track
    /// drops out of the recommendation list immediately; the next refresh
    /// won't return it (its URI joins the playlist's track set).</summary>
    [RelayCommand]
    private async Task AddRecommendationAsync(RecommendedTrackResult? rec)
    {
        var playlistId = PlaylistId;
        if (rec is null || string.IsNullOrEmpty(rec.Uri) || string.IsNullOrEmpty(playlistId))
        {
            _logger?.LogDebug("AddRecommendation skipped: recNull={RecNull}, uri='{Uri}', playlist='{Playlist}'",
                rec is null, rec?.Uri ?? "<null>", playlistId);
            return;
        }
        try
        {
            await _playlistMutationService.AddTracksToPlaylistAsync(playlistId, new[] { rec.Uri })
                .ConfigureAwait(true);
            _recommendedTracks.Remove(rec);
            HasRecommendedTracks = _recommendedTracks.Count > 0;
            // _allTracks lands the new entry via the existing playlist-diff /
            // dealer-update pipeline — no manual local append needed.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AddRecommendationAsync failed for {Uri}", rec.Uri);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show($"Couldn't add to playlist: {ex.Message}", NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>Resets transient state that should not survive a playlist swap
    /// (recommendations + auto-trigger guard).</summary>
    public void ResetForNewPlaylist()
    {
        // Resilient reset: a raw Clear() here runs synchronously during navigation while the
        // bound ItemsRepeater is mid-layout, which throws COMException E_FAIL and hangs the
        // page swap. ReplaceWith mutates the backing list then raises a Reset that retries on a
        // Low-priority dispatcher tick if the control rejects it (same helper used at line ~398).
        Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(_recommendedTracks, []);
        HasRecommendedTracks = false;
        RecommendationsLoadFailed = false;
        _recommendationsAutoTriggeredFor = null;
    }

    public void Dispose()
    {
        Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(_recommendedTracks, []);
    }
}
