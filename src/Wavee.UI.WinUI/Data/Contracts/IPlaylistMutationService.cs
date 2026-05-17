using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Write surface for user-owned playlists — create / delete / follow / rename /
/// edit metadata / mutate tracks / overlay local-only rows. Carved out of
/// <c>ILibraryDataService</c> in Phase 2. Reads still live on
/// <c>ILibraryDataService</c> for now; this carve-out is mutation-only.
/// </summary>
public interface IPlaylistMutationService
{
    /// <summary>
    /// Creates an empty playlist + prepends it to the user's rootlist. The
    /// playlist's <see cref="PlaylistSummaryDto.Id"/> is the freshly minted
    /// <c>spotify:playlist:{id}</c> URI.
    /// </summary>
    Task<PlaylistSummaryDto> CreatePlaylistAsync(string name, IReadOnlyList<string>? trackIds = null, CancellationToken ct = default);

    /// <summary>
    /// Queues a bulk add onto the outbox. Returns once the entry is enqueued —
    /// the actual /playlist/v2/{id}/changes POST happens out-of-band so the
    /// caller never blocks on a thousand-track import. Chunked at 500/Op
    /// internally; cursor-resumable on failure.
    /// </summary>
    Task AddTracksToPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default);

    /// <summary>
    /// Synchronous remove against the playlist's current revision. Posts a single
    /// <c>Rem</c> Op with <c>items_as_key=true</c> so duplicate rows + reorder
    /// races resolve server-side.
    /// </summary>
    Task RemoveTracksFromPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default);

    /// <summary>
    /// Synchronous reorder against the playlist's current revision. <paramref name="length"/>
    /// must be &gt;= 1; <paramref name="toIndex"/> uses Spotify's pre-removal insert convention.
    /// </summary>
    Task ReorderTracksInPlaylistAsync(string playlistId, int fromIndex, int length, int toIndex, CancellationToken ct = default);

    /// <summary>
    /// Renames a playlist via UPDATE_LIST_ATTRIBUTES. Trims the new name.
    /// </summary>
    Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken ct = default);

    /// <summary>
    /// Updates the description via UPDATE_LIST_ATTRIBUTES. Empty/blank clears
    /// the field via the explicit no_value path so the server treats it as
    /// "removed" rather than "set to empty".
    /// </summary>
    Task UpdatePlaylistDescriptionAsync(string playlistId, string description, CancellationToken ct = default);

    /// <summary>
    /// Uploads the JPEG, registers it against the playlist, then writes the
    /// new picture id via UPDATE_LIST_ATTRIBUTES.
    /// </summary>
    Task UpdatePlaylistCoverAsync(string playlistId, byte[] jpegBytes, CancellationToken ct = default);

    /// <summary>
    /// Clears the playlist's custom picture so the auto-mosaic comes back.
    /// </summary>
    Task RemovePlaylistCoverAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Removes the playlist from the user's rootlist — Spotify's desktop client
    /// treats unfollow as the same operation.
    /// </summary>
    Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Adds/removes the playlist from the user's rootlist. Same wire path as
    /// <see cref="DeletePlaylistAsync"/> for the unfollow side and
    /// <see cref="CreatePlaylistAsync"/> for the follow side.
    /// </summary>
    Task SetPlaylistFollowedAsync(string playlistId, bool followed, CancellationToken ct = default);

    /// <summary>
    /// Hits the playlistextender endpoint for "Recommended Songs" — used by
    /// the footer section on the playlist page.
    /// </summary>
    Task<IReadOnlyList<RecommendedTrackResult>> GetPlaylistRecommendationsAsync(
        string playlistUri,
        IReadOnlyList<string>? skipUris = null,
        int numResults = 20,
        CancellationToken ct = default);

    // ── Local-track playlist overlays ──

    /// <summary>
    /// Appends local-track URIs to the playlist's overlay table (Wavee-only,
    /// never round-trips to Spotify).
    /// </summary>
    Task AddLocalTracksToPlaylistAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default);

    /// <summary>
    /// Removes the matching overlay rows. Spotify-side rows are untouched.
    /// </summary>
    Task RemoveLocalOverlayTracksAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default);

    /// <summary>
    /// Returns the overlay rows for this playlist in position order.
    /// </summary>
    Task<IReadOnlyList<PlaylistOverlayRow>> GetPlaylistOverlayRowsAsync(string playlistUri, CancellationToken ct = default);

    /// <summary>
    /// Replaces the overlay positions with the supplied ordering. Used by
    /// drag-reorder in PlaylistViewModel.
    /// </summary>
    Task ReorderPlaylistOverlayAsync(string playlistUri, IReadOnlyList<string> orderedTrackUris, CancellationToken ct = default);
}
