using Wavee.Core.Audio;
using Wavee.Core.Http.Lyrics;
using Wavee.Protocol.Collection;
using Wavee.Protocol.Storage;

namespace Wavee.Core.Http;

/// <summary>
/// Interface for Spotify's spclient HTTP API (metadata, lyrics, context, etc).
/// Enables dependency injection and mocking in tests.
/// </summary>
public interface ISpClient
{
    /// <summary>
    /// Gets the base URL for the SpClient API (e.g., "https://spclient.wg.spotify.com").
    /// </summary>
    string BaseUrl { get; }

    /// <summary>
    /// Gets track metadata.
    /// </summary>
    /// <param name="trackId">Spotify track ID (base62 format, 22 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded track metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    Task<byte[]> GetTrackMetadataAsync(
        string trackId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets album metadata.
    /// </summary>
    /// <param name="albumId">Spotify album ID (base62 format, 22 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded album metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    Task<byte[]> GetAlbumMetadataAsync(
        string albumId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets artist metadata.
    /// </summary>
    /// <param name="artistId">Spotify artist ID (base62 format, 22 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded artist metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    Task<byte[]> GetArtistMetadataAsync(
        string artistId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets episode metadata.
    /// </summary>
    /// <remarks>
    /// Spotify episodes are streamed like tracks - they have file IDs and use
    /// the same CDN/storage mechanism. The metadata includes an audio list
    /// (equivalent to track's file list) with available formats.
    /// </remarks>
    /// <param name="episodeId">Spotify episode ID (base62 format, 22 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded episode metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    Task<byte[]> GetEpisodeMetadataAsync(
        string episodeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets show (podcast) metadata.
    /// </summary>
    /// <param name="showId">Spotify show ID (base62 format, 22 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded show metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    Task<byte[]> GetShowMetadataAsync(
        string showId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves CDN URLs for an audio file.
    /// </summary>
    /// <remarks>
    /// This endpoint returns a list of CDN URLs where the audio file can be downloaded.
    /// The URLs are time-limited and include authentication tokens.
    /// </remarks>
    /// <param name="fileId">The audio file ID (20 bytes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>StorageResolveResponse containing CDN URLs.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    Task<StorageResolveResponse> ResolveAudioStorageAsync(
        FileId fileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Announces device availability via Spotify Connect.
    /// </summary>
    /// <remarks>
    /// This endpoint is used by Spotify Connect to announce device presence.
    /// Spotify uses this information to show the device in the "Available Devices" list.
    /// Requires a connection ID from the dealer WebSocket connection.
    /// </remarks>
    /// <param name="deviceId">Device ID from session config.</param>
    /// <param name="connectionId">Connection ID from dealer WebSocket (from hm://pusher/v1/connections/).</param>
    /// <param name="request">PUT state request with device info and state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    Task<byte[]> PutConnectStateAsync(
        string deviceId,
        string connectionId,
        Protocol.Player.PutStateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a playback event to Spotify's event-service.
    /// </summary>
    /// <remarks>
    /// Events are used for playback reporting (artist payouts).
    /// The event body uses tab-delimited (0x09) fields.
    /// </remarks>
    /// <param name="eventBody">Event body bytes (tab-delimited fields).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    Task PostEventAsync(
        byte[] eventBody,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a context URI to get track list (works for playlists, albums, artists, stations, etc.)
    /// </summary>
    /// <remarks>
    /// Uses the context-resolve API which returns JSON-encoded protobuf.
    /// The returned Context contains pages of tracks that can be loaded lazily.
    /// </remarks>
    /// <param name="contextUri">Spotify context URI (e.g., "spotify:playlist:xxx", "spotify:album:xxx").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Context containing track pages.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    Task<Protocol.Context.Context> ResolveContextAsync(
        string contextUri,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches time-synced lyrics for a track from Spotify's color-lyrics API.
    /// </summary>
    /// <param name="trackId">Track ID in base62 format (e.g., "4xeugB5MqWh0jwvXZPxahq").</param>
    /// <param name="imageUri">Album image URI (e.g., "spotify:image:ab67616d00001e02...").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Lyrics response with timed lines, or null if no lyrics available.</returns>
    Task<LyricsResponse?> GetLyricsAsync(
        string trackId,
        string imageUri,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the next page of tracks using the page URL from a ContextPage.
    /// </summary>
    /// <remarks>
    /// Page URLs typically use the hm:// scheme which needs to be stripped.
    /// </remarks>
    /// <param name="pageUrl">Page URL from ContextPage.PageUrl or NextPageUrl (hm://... format).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>ContextPage containing more tracks.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    Task<Protocol.Context.ContextPage> GetNextPageAsync(
        string pageUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a page of items from a user's collection (liked songs, albums, artists).
    /// </summary>
    /// <param name="username">Spotify username.</param>
    /// <param name="set">Collection set: "collection" (tracks), "albums", "artists".</param>
    /// <param name="paginationToken">Token for next page, null for first page.</param>
    /// <param name="limit">Maximum items per page (default 300).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>PageResponse with items and pagination info.</returns>
    Task<PageResponse> GetCollectionPageAsync(
        string username,
        string set,
        string? paginationToken = null,
        int limit = 300,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets incremental changes to a collection since the last sync.
    /// </summary>
    /// <param name="username">Spotify username.</param>
    /// <param name="set">Collection set: "collection" (tracks), "albums", "artists".</param>
    /// <param name="lastSyncToken">Sync token from previous sync.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>DeltaResponse with changes and new sync token.</returns>
    Task<DeltaResponse> GetCollectionDeltaAsync(
        string username,
        string set,
        string lastSyncToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or removes items from a collection.
    /// </summary>
    /// <param name="username">Spotify username.</param>
    /// <param name="set">Collection set: "collection" (tracks), "albums", "artists".</param>
    /// <param name="items">Items to add/remove (use is_removed flag).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteCollectionAsync(
        string username,
        string set,
        IEnumerable<CollectionItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a playlist's content (metadata and optionally tracks).
    /// </summary>
    /// <remarks>
    /// Uses the playlist v2 API which returns protobuf SelectedListContent.
    /// The rootlist (spotify:user:{username}:rootlist) contains all user playlists.
    /// </remarks>
    /// <param name="playlistUri">Playlist URI (e.g., "spotify:playlist:xxx" or "spotify:user:xxx:rootlist").</param>
    /// <param name="decorate">Fields to include: revision, attributes, length, owner, capabilities.</param>
    /// <param name="start">Start index for tracks (0-based).</param>
    /// <param name="length">Number of tracks to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SelectedListContent with playlist data.</returns>
    Task<Protocol.Playlist.SelectedListContent> GetPlaylistAsync(
        string playlistUri,
        string[]? decorate = null,
        int? start = null,
        int? length = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets changes to a playlist since a specific revision (for incremental sync).
    /// </summary>
    /// <param name="playlistUri">Playlist URI.</param>
    /// <param name="revision">Last known revision (from previous sync).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SelectedListContent with diff information.</returns>
    Task<Protocol.Playlist.SelectedListContent> GetPlaylistDiffAsync(
        string playlistUri,
        byte[] revision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends changes to a playlist.
    /// </summary>
    /// <param name="playlistUri">Playlist URI.</param>
    /// <param name="changes">Changes to apply (adds, removes, moves, attribute updates).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SelectedListContent with updated state.</returns>
    Task<Protocol.Playlist.SelectedListContent> ChangePlaylistAsync(
        string playlistUri,
        Protocol.Playlist.ListChanges changes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a user's profile via the spclient profile endpoint.
    /// Uses Login5 token + client-token (no public Web API, no 429 issues).
    /// </summary>
    Task<SpotifyUserProfile> GetUserProfileAsync(
        string username,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a user's following list via the spclient profile endpoint.
    /// </summary>
    Task<SpotifyFollowingResponse> GetUserFollowingAsync(
        string username,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches extended top track URIs for an artist (beyond the initial overview set).
    /// Returns just URIs — caller must enrich via extended-metadata.
    /// </summary>
    Task<List<string>> GetArtistTopTrackExtensionsAsync(
        string artistUri,
        CancellationToken cancellationToken = default);
}
