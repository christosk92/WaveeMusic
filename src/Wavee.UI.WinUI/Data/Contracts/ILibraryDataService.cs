using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contracts;

public sealed class PodcastEpisodeProgressChangedEventArgs(PodcastEpisodeProgressDto progress, string? aliasUri = null)
    : EventArgs
{
    public PodcastEpisodeProgressDto Progress { get; } = progress;
    public string EpisodeUri => Progress.Uri;
    public string? AliasUri { get; } = aliasUri;

    public bool Matches(string? episodeUri)
        => !string.IsNullOrWhiteSpace(episodeUri) &&
           (string.Equals(episodeUri, EpisodeUri, StringComparison.Ordinal) ||
            string.Equals(episodeUri, AliasUri, StringComparison.Ordinal));
}

/// <summary>
/// Service for retrieving user library data.
/// </summary>
public interface ILibraryDataService
{
    /// <summary>
    /// Gets library statistics for sidebar badges.
    /// </summary>
    Task<LibraryStatsDto> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all library items. Sorting/filtering is done client-side.
    /// </summary>
    Task<IReadOnlyList<LibraryItemDto>> GetAllItemsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets recently played items.
    /// </summary>
    Task<IReadOnlyList<LibraryItemDto>> GetRecentlyPlayedAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Gets user's playlists.
    /// </summary>
    Task<IReadOnlyList<PlaylistSummaryDto>> GetUserPlaylistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all albums in the user's library.
    /// </summary>
    Task<IReadOnlyList<LibraryAlbumDto>> GetAlbumsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all artists in the user's library.
    /// </summary>
    Task<IReadOnlyList<LibraryArtistDto>> GetArtistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets top tracks for a specific artist.
    /// </summary>
    Task<IReadOnlyList<LibraryArtistTopTrackDto>> GetArtistTopTracksAsync(string artistId, CancellationToken ct = default);

    /// <summary>
    /// Gets albums for a specific artist.
    /// </summary>
    Task<IReadOnlyList<LibraryArtistAlbumDto>> GetArtistAlbumsAsync(string artistId, CancellationToken ct = default);

    /// <summary>
    /// Gets all liked/saved songs in the user's library.
    /// </summary>
    Task<IReadOnlyList<LikedSongDto>> GetLikedSongsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets saved podcast episodes in the user's library.
    /// </summary>
    Task<IReadOnlyList<LibraryEpisodeDto>> GetYourEpisodesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets podcast episodes recently seen in Spotify's podcast resumption state.
    /// Includes saved and unsaved episodes when metadata can be resolved.
    /// </summary>
    Task<IReadOnlyList<LibraryEpisodeDto>> GetRecentlyPlayedPodcastEpisodesAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Gets followed podcasts plus podcast shows inferred from saved episodes.
    /// </summary>
    Task<IReadOnlyList<LibraryPodcastShowDto>> GetPodcastShowsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets enriched podcast episode details from Spotify when available,
    /// including recommendations and comments.
    /// </summary>
    Task<PodcastEpisodeDetailDto?> GetPodcastEpisodeDetailAsync(string episodeUri, CancellationToken ct = default);

    /// <summary>
    /// Fetches a subsequent page of public comments for a podcast episode using
    /// the page token returned by an earlier call.
    /// </summary>
    Task<PodcastEpisodeCommentsPageDto?> GetPodcastEpisodeCommentsPageAsync(
        string episodeUri, string? pageToken, CancellationToken ct = default);

    /// <summary>
    /// Fetches a page of replies for a single comment.
    /// </summary>
    Task<PodcastCommentRepliesPageDto?> GetPodcastCommentRepliesAsync(
        string commentUri, string? pageToken, CancellationToken ct = default);

    /// <summary>
    /// Fetches a page of reactions for a single comment or reply.
    /// </summary>
    Task<PodcastCommentReactionsPageDto?> GetPodcastCommentReactionsAsync(
        string uri,
        string? pageToken,
        string? reactionUnicode,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a reply for a public podcast comment. Spotify write support is currently stubbed.
    /// </summary>
    Task<PodcastEpisodeCommentReplyDto> CreatePodcastCommentReplyAsync(
        string commentUri,
        string text,
        CancellationToken ct = default);

    /// <summary>
    /// Adds or updates the current user's emoji reaction for a public podcast comment.
    /// Spotify write support is currently stubbed.
    /// </summary>
    Task ReactToPodcastCommentAsync(
        string commentUri,
        string emoji,
        CancellationToken ct = default);

    /// <summary>
    /// Adds or updates the current user's emoji reaction for a public podcast comment reply.
    /// Spotify write support is currently stubbed.
    /// </summary>
    Task ReactToPodcastCommentReplyAsync(
        string replyUri,
        string emoji,
        CancellationToken ct = default);

    /// <summary>
    /// Gets just the played-state fields needed by podcast library rows.
    /// </summary>
    Task<PodcastEpisodeProgressDto?> GetPodcastEpisodeProgressAsync(
        string episodeUri,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the user's podcast resume point to Spotify Herodotus. Pass
    /// <c>null</c> for <paramref name="resumePosition"/> when the episode is
    /// completed.
    /// </summary>
    Task SavePodcastEpisodeProgressAsync(
        string episodeUri,
        TimeSpan? resumePosition,
        bool completed,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a podcast episode comment. Spotify write support is currently stubbed.
    /// </summary>
    Task<PodcastEpisodeCommentDto> CreatePodcastEpisodeCommentAsync(
        string episodeUri,
        string text,
        CancellationToken ct = default);

    /// <summary>
    /// Gets Spotify-provided content filters for the liked songs page.
    /// </summary>
    Task<IReadOnlyList<LikedSongsFilterDto>> GetLikedSongFiltersAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a new playlist and returns the created playlist summary.
    /// </summary>
    Task<PlaylistSummaryDto> CreatePlaylistAsync(string name, IReadOnlyList<string>? trackIds = null, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed information about a specific playlist.
    /// </summary>
    Task<PlaylistDetailDto> GetPlaylistAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Gets tracks for a specific playlist.
    /// </summary>
    Task<IReadOnlyList<PlaylistTrackDto>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Fetches the playlist's follower count via the popcount endpoint.
    /// Held out of <see cref="GetPlaylistAsync"/> so the detail load isn't blocked
    /// on a stat-only round trip — the VM fires this in parallel and shimmers
    /// the count chip until it arrives. Returns 0 if the count is hidden / unavailable.
    /// </summary>
    Task<long> GetPlaylistFollowerCountAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Fetches the playlist's pre-extracted color palette (Spotify-side image
    /// processing) via the <c>fetchPlaylist</c> Pathfinder query. Reuses the
    /// album-page <see cref="AlbumPalette"/> type — the GraphQL shape is
    /// identical for both surfaces. Returns null when the palette isn't
    /// available (e.g. mosaic-cover playlists with no upstream extraction).
    /// </summary>
    Task<AlbumPalette?> GetPlaylistPaletteAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Toggles whether the current user follows the given playlist. Backend
    /// wire-up is currently stubbed — the call exists so the heart button on
    /// the playlist hero can light up optimistically.
    /// </summary>
    Task SetPlaylistFollowedAsync(string playlistId, bool followed, CancellationToken ct = default);

    /// <summary>
    /// Removes tracks from a playlist.
    /// </summary>
    Task RemoveTracksFromPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default);

    /// <summary>
    /// Renames a playlist owned by the current user.
    /// Maps to Spotify Web API <c>PUT /v1/playlists/{id}</c> (name field).
    /// </summary>
    Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken ct = default);

    /// <summary>
    /// Updates the description of a playlist owned by the current user. Pass an
    /// empty string to clear the description.
    /// Maps to Spotify Web API <c>PUT /v1/playlists/{id}</c> (description field).
    /// </summary>
    Task UpdatePlaylistDescriptionAsync(string playlistId, string description, CancellationToken ct = default);

    /// <summary>
    /// Replaces the playlist cover image. <paramref name="jpegBytes"/> must be a
    /// JPEG ≤256 KB (use <c>PlaylistCoverHelper.PrepareForUploadAsync</c>).
    /// Maps to Spotify Web API <c>PUT /v1/playlists/{id}/images</c> (base64 body).
    /// </summary>
    Task UpdatePlaylistCoverAsync(string playlistId, byte[] jpegBytes, CancellationToken ct = default);

    /// <summary>
    /// Removes a custom cover, reverting to the auto-generated mosaic.
    /// </summary>
    Task RemovePlaylistCoverAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a playlist owned by the current user (Spotify implements this as
    /// the owner unfollowing their own playlist).
    /// Maps to Spotify Web API <c>DELETE /v1/playlists/{id}/followers</c>.
    /// </summary>
    Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Toggles a playlist between owner-only and collaborative. Collaborative
    /// playlists must also be private (the API enforces this).
    /// Maps to Spotify Web API <c>PUT /v1/playlists/{id}</c> (collaborative field).
    /// </summary>
    Task SetPlaylistCollaborativeAsync(string playlistId, bool collaborative, CancellationToken ct = default);

    /// <summary>
    /// Lists current members of a collaborative playlist. Empty result when the
    /// caller lacks <c>canAdministratePermissions</c> AND the playlist isn't open
    /// to the caller as a collaborator. Maps to
    /// <c>GET /playlist-permission/v1/playlist/{id}/permission/members</c>.
    /// </summary>
    Task<IReadOnlyList<PlaylistMemberResult>> GetPlaylistMembersAsync(
        string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Sets a member's permission role. Owner-administered playlists only.
    /// Maps to <c>PUT /playlist-permission/v1/playlist/{id}/permission/members/{userId}</c>.
    /// </summary>
    Task SetPlaylistMemberRoleAsync(
        string playlistId, string memberUserId, PlaylistMemberRole role, CancellationToken ct = default);

    /// <summary>
    /// Removes a member from the playlist's permission list (revokes access).
    /// Maps to <c>DELETE /playlist-permission/v1/playlist/{id}/permission/members/{userId}</c>.
    /// </summary>
    Task RemovePlaylistMemberAsync(
        string playlistId, string memberUserId, CancellationToken ct = default);

    /// <summary>
    /// Generates a shareable invite link granting the supplied role for the supplied
    /// duration. Maps to <c>POST /playlist-permission/v1/playlist/{id}/permission-grant</c>.
    /// </summary>
    Task<PlaylistInviteLink> CreatePlaylistInviteLinkAsync(
        string playlistId, PlaylistMemberRole grantedRole, TimeSpan ttl, CancellationToken ct = default);

    // ── Local-track playlist overlays ──
    //
    // Wavee-only rows attached to a Spotify playlist (or, in v2, a fully local
    // playlist). These never round-trip to Spotify — they live in the
    // playlist_overlay_items SQLite table and are merged into the rendered
    // track list in PlaylistViewModel.

    /// <summary>
    /// Adds the given local track URIs to the playlist as Wavee-only overlay
    /// rows. Items are appended at the end of the current overlay sequence.
    /// </summary>
    Task AddLocalTracksToPlaylistAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default);

    /// <summary>
    /// Removes overlay rows matching the given track URIs from the playlist.
    /// Spotify-side rows are not affected.
    /// </summary>
    Task RemoveLocalOverlayTracksAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default);

    /// <summary>
    /// Returns the URIs of overlay rows on this playlist in position order,
    /// each with its absolute added-at timestamp.
    /// </summary>
    Task<IReadOnlyList<PlaylistOverlayRow>> GetPlaylistOverlayRowsAsync(string playlistUri, CancellationToken ct = default);

    /// <summary>
    /// Replaces the overlay-row positions for the given playlist with the
    /// supplied ordering. Used by drag-reorder in PlaylistViewModel.
    /// </summary>
    Task ReorderPlaylistOverlayAsync(string playlistUri, IReadOnlyList<string> orderedTrackUris, CancellationToken ct = default);

    /// <summary>
    /// Event raised when playlists change (created, deleted, updated).
    /// </summary>
    event EventHandler? PlaylistsChanged;

    /// <summary>
    /// Event raised when any library data changes (sync complete, Dealer delta, user action).
    /// Subscribe to refresh UI (sidebar badges, library pages, etc.).
    /// </summary>
    event EventHandler? DataChanged;

    /// <summary>
    /// Event raised when a podcast episode resume point changes. This is scoped
    /// to progress only and should not trigger a full library reload.
    /// </summary>
    event EventHandler<PodcastEpisodeProgressChangedEventArgs>? PodcastEpisodeProgressChanged;

    /// <summary>
    /// Requests a full library sync when local data appears to be missing.
    /// No-ops if a sync is already in progress. The DataChanged event fires
    /// when the sync completes so callers do not need to poll.
    /// </summary>
    void RequestSyncIfEmpty();
}
